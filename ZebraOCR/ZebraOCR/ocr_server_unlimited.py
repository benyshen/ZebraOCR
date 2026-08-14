#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
OCR Server - Unlimited-OCR 专用推理服务
本地 HTTP 服务，供 ZebraOCR (C# WPF) 调用。

用法:
    python ocr_server_unlimited.py [--port 5100] [--model <路径>] [--force-cpu]

加载策略:
    - 显存 >= 8GB : BF16 全量加载
    - 显存 < 8GB  : NF4 4-bit 量化加载
    - 无 GPU      : CPU 加载

HTTP API:
    GET  /health   健康检查
    POST /recognize  {"image_path": "...", "prompt": "...", "image_mode": "gundam|base", "max_length": 8192}
"""

import sys
if hasattr(sys.stdout, 'reconfigure'):
    try:
        sys.stdout.reconfigure(encoding='utf-8', errors='replace')
        sys.stderr.reconfigure(encoding='utf-8', errors='replace')
    except Exception:
        pass

import argparse
import json
import os
import re
import shutil
import tempfile
import threading
import time
from http.server import HTTPServer, BaseHTTPRequestHandler

MODEL_NAME = r'D:\AI\OCR-Scane\Unlimited-OCR'   # 默认模型路径
MODEL_TYPE = 'unlimited'
model = None
tokenizer = None
load_error = None
load_progress = ""
device = "cpu"


class OCRHandler(BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path == '/health':
            self.send_response(200)
            self.send_header('Content-type', 'application/json')
            self.end_headers()
            resp = {
                'status': 'ok',
                'model_loaded': model is not None,
                'model_type': MODEL_TYPE,
                'model_name': MODEL_NAME,
                'device': device,
                'load_progress': load_progress,
                'error': load_error,
            }
            self.wfile.write(json.dumps(resp, ensure_ascii=False).encode('utf-8'))
        else:
            self.send_response(404)
            self.end_headers()

    def do_POST(self):
        if self.path != '/recognize':
            self.send_response(404)
            self.end_headers()
            return

        content_length = int(self.headers.get('Content-Length', 0))
        post_data = self.rfile.read(content_length)
        try:
            data = json.loads(post_data.decode('utf-8'))
        except Exception as e:
            self._send_json(400, {'success': False, 'error': 'Invalid JSON: ' + str(e)})
            return

        image_path = data.get('image_path', '')
        prompt = data.get('prompt', '')
        image_mode = data.get('image_mode', 'gundam')
        max_length = int(data.get('max_length', 2048))
        max_length = min(max_length, 2048)  # cap for 4GB VRAM

        if not os.path.exists(image_path):
            self._send_json(400, {'success': False, 'error': 'Image file not found'})
            return

        if model is None:
            self._send_json(503, {'success': False, 'error': 'Model not loaded yet: ' + str(load_error or load_progress)})
            return

        try:
            t0 = time.time()
            result_text = run_inference(image_path, prompt, image_mode, max_length)
            elapsed = time.time() - t0
            self._send_json(200, {'success': True, 'result': result_text, 'elapsed_sec': round(elapsed, 2)})
        except Exception as e:
            import traceback
            traceback.print_exc()
            print('[ERROR] recognize failed: ' + str(e), flush=True)
            try:
                self._send_json(500, {'success': False, 'error': str(e)})
            except Exception:
                pass  # client already disconnected
        finally:
            # 推理后释放显存缓存，避免 4GB 显存连续识别 OOM
            try:
                import torch
                if device == "cuda":
                    torch.cuda.empty_cache()
            except Exception:
                pass

    def _send_json(self, status, obj):
        body = json.dumps(obj, ensure_ascii=False).encode('utf-8')
        self.send_response(status)
        self.send_header('Content-type', 'application/json; charset=utf-8')
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, fmt, *args):
        pass  # 关闭请求日志，保持控制台干净

# ==================== 推理 ====================

_INTRA_REPEAT_RE = re.compile(r'(.{2,10}?)\1{4,}')


def _compact_text(s):
    return re.sub(r'\s+', '', s)


def has_intra_block_repeat(content):
    """Detect a phrase repeated >= 5 times inside one det-block text."""
    if not content:
        return False
    compact = _compact_text(content)
    if len(compact) < 10:
        return False
    m = _INTRA_REPEAT_RE.search(compact)
    if not m:
        return False
    unit = m.group(1)
    return len(unit) >= 2


class RepetitionStopCriteria:
    """Detect repeated det-block output (infinite loop) and stop generation early.

    On some images Unlimited-OCR enters a loop: it repeatedly emits the same
    text content while coordinates drift by 1-2 px (or oscillate between two
    nearby clusters), so no_repeat_ngram cannot catch it. This criteria decodes
    recent generated tokens, parses det blocks, and stops as soon as the same
    (rounded-coords + text) block is seen twice, or the same text appears many
    times in a small window with repeated boxes.
    """
    def __init__(self, tokenizer, window_tokens=2048, max_dupes=2, max_repeats=6, max_consecutive=4, round_px=8, check_every=4):
        self.tokenizer = tokenizer
        self.window_tokens = window_tokens
        self.max_dupes = max_dupes
        self.max_repeats = max_repeats
        self.max_consecutive = max_consecutive
        self.round_px = round_px
        self.check_every = check_every
        self._step = 0
        self._pattern = re.compile(
            r'<\|det\|>\s*([A-Za-z_][\w-]*)\s*\[([0-9.,\s-]+)\]\s*<\|/det\|>([^<]*)'
        )

    def __call__(self, input_ids, scores, **kwargs):
        self._step += 1
        if self._step % self.check_every != 0:
            return False
        seq = input_ids[0]
        if seq.shape[0] < 50:
            return False
        tail = seq[-self.window_tokens:]
        text = self.tokenizer.decode(tail, skip_special_tokens=False)
        blocks = []
        for m in self._pattern.finditer(text):
            num_strs = re.findall(r'[\d.]+', m.group(2))
            if len(num_strs) < 4:
                continue
            try:
                coords = [int(round(float(x))) for x in num_strs[:4]]
            except Exception:
                continue
            content = m.group(3).strip()
            if not content:
                continue
            blocks.append((m.group(1).strip(), coords, content))
        if len(blocks) < 2:
            return False

        # 1) same rounded-coords + text seen twice -> definite loop
        seen = set()
        for btype, coords, content in blocks:
            rnd = tuple((c // self.round_px) * self.round_px for c in coords)
            key = (btype, rnd, content)
            if key in seen:
                return True
            seen.add(key)

        # 2) same text repeated many times with re-used boxes -> loop
        if len(blocks) >= self.max_repeats:
            tail_blocks = blocks[-self.max_repeats:]
            if len(set(b[2] for b in tail_blocks)) == 1:
                boxes = set()
                for _, coords, _ in tail_blocks:
                    rnd = tuple((c // self.round_px) * self.round_px for c in coords)
                    boxes.add(rnd)
                if len(boxes) < len(tail_blocks):
                    return True

        # 3) same text content repeated consecutively many times (coords drifting)
        #    -> clear loop signal even when coordinates change smoothly
        if len(blocks) >= self.max_consecutive:
            tail_blocks = blocks[-self.max_consecutive:]
            contents = [b[2] for b in tail_blocks]
            if len(set(contents)) == 1 and contents[0]:
                return True

        # 4) a single block repeats the same phrase many times inside itself
        #    (e.g. "□痰栓症状" x20) -> intra-block repetition loop
        for _, _, content in blocks:
            if has_intra_block_repeat(content):
                return True
        return False


def make_stopping_criteria(tokenizer):
    """Wrap RepetitionStopCriteria in a StoppingCriteriaList (required by generate())."""
    try:
        from transformers import StoppingCriteriaList
        return StoppingCriteriaList([RepetitionStopCriteria(tokenizer)])
    except Exception:
        return None


def collapse_intra_block_repeat(content):
    """Collapse a phrase repeated >= 5 times inside one block to a single copy."""
    if not content:
        return content
    compact = _compact_text(content)
    m = _INTRA_REPEAT_RE.search(compact)
    if not m:
        return content
    unit = m.group(1)
    if len(unit) < 2:
        return content
    pat = re.compile(r'(?P<u>' + re.escape(unit) + r')(?:[ \t\u3000]*' + re.escape(unit) + r'){4,}')
    return pat.sub(lambda mm: mm.group('u'), content)


def dedupe_result(text):
    """Remove duplicated det blocks (same coords+text, or consecutive same text)
    and trailing incomplete fragments."""
    if '<|det|>' not in text:
        # result.md 是纯文本（det 标记已被模型 save_results 剥离）：
        # 逐行压缩行内重复短语，并删除连续完全相同的行（漂移循环产物）
        lines = text.split('\n')
        out = []
        last_line = None
        for line in lines:
            collapsed = collapse_intra_block_repeat(line)
            stripped = collapsed.strip()
            if stripped and stripped == (last_line.strip() if last_line else None):
                continue  # 连续相同行：只保留第一个
            out.append(collapsed)
            last_line = collapsed
        return '\n'.join(out)
    # strip a trailing un-closed <|det|> fragment (generation stopped mid-block)
    text = re.sub(r'<\|det\|>[^<]*$', '', text)
    pattern = re.compile(r'(<\|det\|>.*?<\|/det\|>[^<]*)')
    parts = pattern.split(text)
    seen = set()
    out = []
    last_text = None
    for part in parts:
        if part.startswith('<|det|>'):
            key = re.sub(r'\s+', ' ', part).strip()
            # exact duplicate (same coords + text): always drop
            if key in seen:
                continue
            seen.add(key)
            # consecutive blocks with same text content (coords drifting): drop
            m = re.search(r'<\|/det\|>([^<]*)$', part)
            cur_text = m.group(1).strip() if m else ''
            if cur_text and cur_text == last_text:
                continue
            last_text = cur_text
            # collapse intra-block repetition (e.g. "□痰栓症状" x20)
            new_part = collapse_intra_block_repeat(part)
            if new_part != part:
                out.append(new_part)
                continue
        elif part:
            # real text between blocks: reset consecutive-text tracking
            last_text = None
        out.append(part)
    return ''.join(out)


def run_inference(image_path, prompt, image_mode, max_length):
    """Unlimited-OCR 推理（gundam/base 两种模式）"""
    if not prompt:
        prompt = '<image>document parsing.'
    output_path = tempfile.mkdtemp(prefix='uocr_out_')
    try:
        if image_mode == 'gundam':
            model.infer(
                tokenizer,
                prompt=prompt,
                image_file=image_path,
                output_path=output_path,
                base_size=1024,
                image_size=640,
                crop_mode=True,
                max_length=max_length,
                no_repeat_ngram_size=35,
                ngram_window=128,
                save_results=True,
                stopping_criteria=make_stopping_criteria(tokenizer),
            )
        else:
            model.infer(
                tokenizer,
                prompt=prompt,
                image_file=image_path,
                output_path=output_path,
                image_size=1024,
                max_length=max_length,
                no_repeat_ngram_size=35,
                ngram_window=1024,
                save_results=True,
                stopping_criteria=make_stopping_criteria(tokenizer),
            )

        result_text = ''
        for fname in sorted(os.listdir(output_path)):
            fpath = os.path.join(output_path, fname)
            if os.path.isfile(fpath) and fname.lower().endswith(('.txt', '.md')):
                with open(fpath, 'r', encoding='utf-8') as f:
                    result_text += f.read()
        result_text = dedupe_result(result_text)
        return result_text.strip()
    finally:
        shutil.rmtree(output_path, ignore_errors=True)


# ==================== 加载 ====================

def pick_load_strategy(force_cpu=False):
    """根据显存选择加载策略"""
    global device
    try:
        import torch
        if force_cpu or not torch.cuda.is_available():
            device = "cpu"
            return {"dtype": None, "quant": None, "device": "cpu", "reason": "no GPU / force-cpu"}
        free_mem_gb = torch.cuda.get_device_properties(0).total_memory / (1024 ** 3)
        device = "cuda"
        if free_mem_gb >= 8:
            return {"dtype": "bf16", "quant": None, "device": "cuda",
                    "reason": "GPU %.1fGB >= 8GB, BF16 full load" % free_mem_gb}
        return {"dtype": "bf16", "quant": "nf4", "device": "cuda",
                "reason": "GPU %.1fGB < 8GB, use 4-bit (NF4) quantized load" % free_mem_gb}
    except Exception as e:
        device = "cpu"
        return {"dtype": None, "quant": None, "device": "cpu", "reason": "detect failed: " + str(e)}


def load_model(force_cpu=False):
    global model, tokenizer, load_error, load_progress, device
    try:
        import torch
        from transformers import AutoModel, AutoTokenizer

        strategy = pick_load_strategy(force_cpu)
        load_progress = "loading strategy: " + strategy["reason"]
        print('[%s] 加载策略: %s' % (time.strftime("%H:%M:%S"), strategy["reason"]), flush=True)

        print('[%s] 正在加载模型 %s ...' % (time.strftime("%H:%M:%S"), MODEL_NAME), flush=True)
        load_progress = "downloading/loading model weights..."

        tokenizer = AutoTokenizer.from_pretrained(MODEL_NAME, trust_remote_code=True)

        load_kwargs = dict(trust_remote_code=True, use_safetensors=True)

        if strategy["quant"] == "nf4":
            from transformers import BitsAndBytesConfig
            load_progress = "4-bit quantized load (NF4)..."
            bnb_config = BitsAndBytesConfig(
                load_in_4bit=True,
                bnb_4bit_quant_type="nf4",
                bnb_4bit_use_double_quant=True,
                bnb_4bit_compute_dtype=torch.bfloat16,
            )
            load_kwargs["quantization_config"] = bnb_config
            print('[%s] 使用 4-bit (NF4) 量化以适应 4GB 显存' % time.strftime("%H:%M:%S"), flush=True)
        else:
            load_kwargs["torch_dtype"] = torch.bfloat16
            print('[%s] 使用 BF16 加载' % time.strftime("%H:%M:%S"), flush=True)

        model = AutoModel.from_pretrained(MODEL_NAME, **load_kwargs)
        model = model.eval()

        if strategy["device"] == "cuda" and strategy["quant"] is None:
            model = model.cuda()

        device = strategy["device"]
        load_progress = "loaded"
        print('[%s] 模型加载完成，设备: %s' % (time.strftime("%H:%M:%S"), device), flush=True)
    except Exception as e:
        load_error = str(e)
        load_progress = "failed"
        print('[%s] 模型加载失败: %s' % (time.strftime("%H:%M:%S"), e), flush=True)


def main():
    global MODEL_NAME
    parser = argparse.ArgumentParser(description='ZebraOCR Unlimited-OCR 本地推理服务')
    parser.add_argument('--port', type=int, default=5100)
    parser.add_argument('--model', type=str, default=None, help='模型目录路径')
    parser.add_argument('--force-cpu', action='store_true', help='强制使用 CPU 推理')
    args = parser.parse_args()

    if args.model:
        MODEL_NAME = args.model
    print('[%s] 模型类型: %s | 模型: %s' % (time.strftime("%H:%M:%S"), MODEL_TYPE, MODEL_NAME), flush=True)

    # 后台线程加载模型
    t = threading.Thread(target=load_model, kwargs={'force_cpu': args.force_cpu}, daemon=True)
    t.start()

    server = HTTPServer(('0.0.0.0', args.port), OCRHandler)
    print('[%s] OCR 服务已监听端口 %s' % (time.strftime("%H:%M:%S"), args.port), flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()


if __name__ == '__main__':
    main()
