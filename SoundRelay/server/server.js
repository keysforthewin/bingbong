require('dotenv').config();
const express = require('express');
const http = require('http');
const { WebSocketServer } = require('ws');

const HTTP_PORT = process.env.HTTP_PORT || 3000;
const WS_PORT = process.env.WS_PORT || 8080;

const app = express();
const httpServer = http.createServer(app);

const wsServer = http.createServer();
const wss = new WebSocketServer({ server: wsServer });

const clients = new Set();

wss.on('connection', (ws) => {
  clients.add(ws);
  console.log(`WebSocket client connected (total: ${clients.size})`);
  ws.on('close', () => {
    clients.delete(ws);
    console.log(`Client disconnected (total: ${clients.size})`);
  });
  ws.on('error', () => clients.delete(ws));
});

app.get('/bingbong/:sound', (req, res) => {
  const sound = req.params.sound;
  const message = `Play ${sound}`;
  let sent = 0;
  for (const client of clients) {
    if (client.readyState === 1) {
      client.send(message);
      sent++;
    }
  }
  console.log(`▶ ${message} → ${sent} client(s)`);
  res.json({ sound, clients: sent });
});

httpServer.listen(HTTP_PORT, () => console.log(`HTTP server on port ${HTTP_PORT}`));
wsServer.listen(WS_PORT, () => console.log(`WebSocket server on port ${WS_PORT}`));
