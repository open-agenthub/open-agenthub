# Stand-in Git host for the clone retry proof: a bare repository with branch main over Git's
# dumb HTTP protocol, and /private answering 401 like a host refusing the credentials.
import http.server, os, subprocess

root = "/tmp/srv"
repo = root + "/repo.git"
if not os.path.isdir(repo):
    os.makedirs(root, exist_ok=True)
    seed = "/tmp/seed"
    run = lambda *a: subprocess.run(a, check=True)
    run("git", "init", "-q", "--initial-branch=main", seed)
    run("git", "-C", seed, "-c", "user.name=Proof", "-c", "user.email=proof@example.com", "commit", "-q", "--allow-empty", "-m", "seed")
    run("git", "clone", "-q", "--bare", seed, repo)
    run("git", "-C", repo, "update-server-info")

class Handler(http.server.SimpleHTTPRequestHandler):
    def __init__(self, *a, **k):
        super().__init__(*a, directory=root, **k)
    def do_GET(self):
        if self.path.startswith("/private"):
            self.send_response(401)
            self.send_header("WWW-Authenticate", 'Basic realm="proof"')
            self.end_headers()
            return
        super().do_GET()

http.server.ThreadingHTTPServer(("", 8080), Handler).serve_forever()
