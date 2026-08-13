#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
OCR Server - baidu/Unlimited-OCR ??????
?? HTTP ??????????

??:
    python ocr_server.py --port 5100 [--model baidu/Unlimited-OCR] [--force-cpu]

??????:
    - ?? >= 8GB : BF16 ????? GPU
    - ?? <  8GB : 4-bit ?? (NF4) ??? GPU (? 2~3GB)
    - ? GPU      : CPU ?? (??)
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
import shutil
import sys
import tempfile
import threading
import time
from http.server import HTTPServer, BaseHTTPRequestHandler

MODEL_NAME = r'D:\AI\OCR-Scane\Unlimited-OCR'  # ??????
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
                'device': device,
                'load_progress': load_progress,
                'error': load_error,
            }
            self.wfile.write(json.dumps(resp).encode())
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
            self._send_json(400, {'success': False, 'error': f'Invalid JSON: {e}'})
            return

        image_path = data.get('image_path', '')
        prompt = data.get('prompt', '<image>document parsing.')
        image_mode = data.get('image_mode', 'gundam')
        max_length = int(data.get('max_length', 32768))

        if not os.path.exists(image_path):
            self._send_json(400, {'success': False, 'error': 'Image file not found'})
            return

        if model is None:
            self._send_json(503, {'success': False, 'error': f'Model not loaded yet: {load_error or load_progress}'})
            return

        try:
            t0 = time.time()
            result_text = run_inference(image_path, prompt, image_mode, max_length)
            elapsed = time.time() - t0
            self._send_json(200, {'success': True, 'result': result_text, 'elapsed_sec': round(elapsed, 2)})
        except Exception as e:
            import traceback
            traceback.print_exc()
            print(f'[ERROR] recognize failed: {e}', flush=True)
            self._send_json(500, {'success': False, 'error': str(e)})

    def _send_json(self, status, obj):
        body = json.dumps(obj, ensure_ascii=False).encode('utf-8')
        self.send_response(status)
        self.send_header('Content-type', 'application/json; charset=utf-8')
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, fmt, *args):
        pass  # ????


def run_inference(image_path, prompt, image_mode, max_length):
    """?????????????"""
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
            )

        result_text = ''
        for fname in sorted(os.listdir(output_path)):
            fpath = os.path.join(output_path, fname)
            if os.path.isfile(fpath) and fname.lower().endswith(('.txt', '.md')):
                with open(fpath, 'r', encoding='utf-8') as f:
                    result_text += f.read()
        return result_text.strip()
    finally:
        shutil.rmtree(output_path, ignore_errors=True)


def pick_load_strategy(force_cpu=False):
    """??????????"""
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
                    "reason": f"GPU {free_mem_gb:.1f}GB >= 8GB, BF16 full load"}
        # ???? 8GB -> 4-bit ??
        return {"dtype": "bf16", "quant": "nf4", "device": "cuda",
                "reason": f"GPU {free_mem_gb:.1f}GB < 8GB, use 4-bit (NF4) quantized load"}
    except Exception as e:
        device = "cpu"
        return {"dtype": None, "quant": None, "device": "cpu", "reason": f"detect failed: {e}"}


def load_model(force_cpu=False):
    global model, tokenizer, load_error, load_progress
    try:
        import torch
        from transformers import AutoModel, AutoTokenizer

        strategy = pick_load_strategy(force_cpu)
        load_progress = f"loading strategy: {strategy['reason']}"
        print(f'[{time.strftime("%H:%M:%S")}] ????: {strategy["reason"]}', flush=True)

        print(f'[{time.strftime("%H:%M:%S")}] ????/???? {MODEL_NAME} ...', flush=True)
        load_progress = "downloading/loading model weights..."

        tokenizer = AutoTokenizer.from_pretrained(MODEL_NAME, trust_remote_code=True)

        load_kwargs = dict(
            trust_remote_code=True,
            use_safetensors=True,
        )

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
            print(f'[{time.strftime("%H:%M:%S")}] ?? 4-bit (NF4) ??????? 4GB ??', flush=True)
        else:
            load_kwargs["torch_dtype"] = torch.bfloat16
            print(f'[{time.strftime("%H:%M:%S")}] ?? BF16 ??', flush=True)

        model = AutoModel.from_pretrained(MODEL_NAME, **load_kwargs)
        model = model.eval()

        if strategy["device"] == "cuda" and strategy["quant"] is None:
            model = model.cuda()

        load_progress = "loaded"
        print(f'[{time.strftime("%H:%M:%S")}] ???????????: {device}', flush=True)
    except Exception as e:
        load_error = str(e)
        load_progress = "failed"
        print(f'[{time.strftime("%H:%M:%S")}] ??????: {e}', flush=True)


def main():
    global MODEL_NAME
    parser = argparse.ArgumentParser(description='Unlimited-OCR Local Server')
    parser.add_argument('--port', type=int, default=5100)
    parser.add_argument('--model', type=str, default=None)
    parser.add_argument('--force-cpu', action='store_true', help='???? CPU ??')
    args = parser.parse_args()

    if args.model:
        MODEL_NAME = args.model

    # ????????
    t = threading.Thread(target=load_model, kwargs={'force_cpu': args.force_cpu}, daemon=True)
    t.start()

    server = HTTPServer(('0.0.0.0', args.port), OCRHandler)
    print(f'[{time.strftime("%H:%M:%S")}] OCR ???????? {args.port}', flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()


if __name__ == '__main__':
    main()
