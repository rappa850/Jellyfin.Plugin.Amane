"""隔离图片源：生成有效 PNG，不请求任何第三方站点。"""
import argparse
import http.server
import struct
import zlib


def chunk(kind, data):
    return struct.pack('!I', len(data)) + kind + data + struct.pack('!I', zlib.crc32(kind + data))


PNG = (b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('!IIBBBBB', 64, 96, 8, 2, 0, 0, 0))
       + chunk(b'IDAT', zlib.compress((b'\x00' + bytes([30, 90, 150]) * 64) * 96)) + chunk(b'IEND', b''))


class Handler(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        self.send_response(200)
        self.send_header('Content-Type', 'image/png')
        self.send_header('Content-Length', str(len(PNG)))
        self.end_headers()
        self.wfile.write(PNG)

    def log_message(self, fmt, *args):
        # 只记录路径与状态；永不记录鉴权 header。
        print(fmt % args, flush=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--bind', default='127.0.0.1')
    parser.add_argument('--port', type=int, default=18101)
    args = parser.parse_args()
    http.server.ThreadingHTTPServer((args.bind, args.port), Handler).serve_forever()
