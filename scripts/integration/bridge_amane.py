#!/usr/bin/env python3
"""把 Docker bridge 上的测试请求转发到 Win11 上仅监听回环地址的 Amane。"""

import argparse
import select
import socket
import socketserver


class Handler(socketserver.BaseRequestHandler):
    def handle(self) -> None:
        try:
            upstream = socket.create_connection(
                (self.server.upstream_host, self.server.upstream_port), timeout=5
            )
        except OSError:
            return

        peers = (self.request, upstream)
        open_peers = set(peers)
        try:
            while open_peers:
                readable, _, _ = select.select(tuple(open_peers), [], [], 300)
                if not readable:
                    return
                for source in readable:
                    data = source.recv(65536)
                    if not data:
                        open_peers.remove(source)
                        target = upstream if source is self.request else self.request
                        try:
                            target.shutdown(socket.SHUT_WR)
                        except OSError:
                            pass
                        continue
                    (upstream if source is self.request else self.request).sendall(data)
        finally:
            upstream.close()


class Server(socketserver.ThreadingTCPServer):
    allow_reuse_address = True
    daemon_threads = True


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--listen", required=True, help="Docker bridge gateway IPv4")
    parser.add_argument("--port", type=int, default=18101)
    parser.add_argument("--upstream", default="127.0.0.1")
    parser.add_argument("--upstream-port", type=int, default=18100)
    args = parser.parse_args()

    server = Server((args.listen, args.port), Handler)
    server.upstream_host = args.upstream
    server.upstream_port = args.upstream_port
    print(f"forwarding {args.listen}:{args.port} to {args.upstream}:{args.upstream_port}", flush=True)
    server.serve_forever()


if __name__ == "__main__":
    main()
