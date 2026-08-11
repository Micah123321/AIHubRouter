const http = require("node:http");
const fs = require("node:fs");
const path = require("node:path");

const port = Number(process.env.AIHUB_UI_FIXTURE_PORT || 5099);
const root = path.resolve(__dirname, "../../src/AIHubRouter.Web/wwwroot");
const fixture = fs.readFileSync(
  path.resolve(__dirname, "fixtures/reliability-dashboard.json"),
  "utf8"
);
const contentTypes = {
  ".css": "text/css; charset=utf-8",
  ".html": "text/html; charset=utf-8",
  ".js": "text/javascript; charset=utf-8",
  ".png": "image/png"
};

http.createServer((request, response) => {
  if (request.url === "/api/auth/status") {
    response.writeHead(200, { "Content-Type": "application/json" });
    response.end('{"authenticated":true}');
    return;
  }
  if (request.url === "/api/dashboard" || request.url === "/api/actions/reliability-check") {
    response.writeHead(request.url === "/api/dashboard" ? 200 : 202, {
      "Content-Type": "application/json"
    });
    response.end(fixture);
    return;
  }

  const requestPath = request.url === "/" ? "/index.html" : request.url.split("?", 1)[0];
  const filePath = path.resolve(root, `.${requestPath}`);
  if (!filePath.startsWith(`${root}${path.sep}`) || !fs.existsSync(filePath)) {
    response.writeHead(404);
    response.end("Not found");
    return;
  }

  response.writeHead(200, {
    "Content-Type": contentTypes[path.extname(filePath)] || "application/octet-stream"
  });
  fs.createReadStream(filePath).pipe(response);
}).listen(port, "127.0.0.1", () => {
  process.stdout.write(`fixture server listening on http://127.0.0.1:${port}\n`);
});
