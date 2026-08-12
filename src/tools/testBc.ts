import fs from 'node:fs';
import path from 'node:path';
import readline from 'node:readline';
import WebSocket from 'ws';

function defaultUrl(): string {
  try {
    const raw = JSON.parse(fs.readFileSync(path.resolve(process.cwd(), 'config', 'vpbridge.json'), 'utf8')) as any;
    const port = Number(raw?.server?.port ?? 8170);
    const mode = raw?.server?.mode === 'all' ? 'all' : 'local';
    const key = String(raw?.security?.apiKey ?? '').trim();
    const auth = mode === 'all' && key ? `?apiKey=${encodeURIComponent(key)}` : '';
    return `ws://127.0.0.1:${port}/bc${auth}`;
  } catch {
    return 'ws://127.0.0.1:8170/bc';
  }
}

const url = process.argv[2] ?? defaultUrl();
const ws = new WebSocket(url);
const rl = readline.createInterface({ input: process.stdin, output: process.stdout, prompt: 'BC> ' });

ws.on('open', () => {
  console.log(`TEST BC connected: ${url.replace(/apiKey=[^&]+/, 'apiKey=***')}`);
  console.log('Type a message and press Enter to send BC→VP.');
  console.log('Messages received from VP will be shown as [BC RECEIVED].');
  console.log('Type /exit to quit.');
  rl.prompt();
});

ws.on('message', (data, isBinary) => {
  if (isBinary) console.log('\n[BC RECEIVED BINARY]');
  else console.log(`\n[BC RECEIVED] ${data.toString()}`);
  rl.prompt();
});

rl.on('line', (line) => {
  const value = line.trim();
  if (value === '/exit') { ws.close(); rl.close(); return; }
  if (!value) { rl.prompt(); return; }
  if (ws.readyState !== WebSocket.OPEN) { console.log('WebSocket is not connected.'); rl.prompt(); return; }
  ws.send(value); console.log(`[BC SENT] ${value}`); rl.prompt();
});

ws.on('close', () => { console.log('TEST BC disconnected.'); rl.close(); process.exit(0); });
ws.on('error', (err) => { console.error(`TEST BC error: ${err.message}`); });
