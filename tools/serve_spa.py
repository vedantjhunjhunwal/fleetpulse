"""Serves the built dashboard (dist/dashboard/browser) with SPA fallback: python serve_spa.py [port]."""
import http.server
import os
import sys

ROOT = os.path.join(os.path.dirname(__file__), "..", "dashboard", "dist", "dashboard", "browser")


class Handler(http.server.SimpleHTTPRequestHandler):
    def __init__(self, *a, **kw):
        super().__init__(*a, directory=ROOT, **kw)

    def send_head(self):
        path = self.translate_path(self.path)
        if not os.path.exists(path):
            self.path = "/index.html"  # client-side route
        return super().send_head()

    def log_message(self, *args):
        pass


if __name__ == "__main__":
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 4200
    http.server.ThreadingHTTPServer(("127.0.0.1", port), Handler).serve_forever()
