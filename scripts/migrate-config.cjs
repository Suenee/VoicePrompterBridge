const fs = require('fs')
const path = require('path')
const crypto = require('crypto')

const configPath = path.resolve(process.cwd(), 'config', 'vpbridge.json')
const backupPath = configPath + '.pre-v0.5.2.bak'

function asInt(value, fallback, min, max) {
  const n = Number(value)
  if (!Number.isInteger(n) || n < min || (max !== undefined && n > max)) return fallback
  return n
}

let raw = {}
if (fs.existsSync(configPath)) {
  try {
    raw = JSON.parse(fs.readFileSync(configPath, 'utf8'))
    if (!fs.existsSync(backupPath)) fs.copyFileSync(configPath, backupPath)
  } catch (err) {
    console.error('ERROR: Existing config\\vpbridge.json is not valid JSON.')
    console.error(String(err && err.message ? err.message : err))
    process.exit(1)
  }
}

const oldServer = raw.server || {}
const oldSecurity = raw.security || {}
const oldQueue = raw.queue || {}
const oldLogging = raw.logging || {}

const mode = oldServer.mode === 'all' ? 'all' : 'local'
let apiKey = String(oldSecurity.apiKey || '').trim()
if (mode === 'all' && !/^[a-fA-F0-9]{64}$/.test(apiKey)) {
  apiKey = crypto.randomBytes(32).toString('hex')
  console.log('Generated missing API key for All Interfaces mode.')
}

const cfg = {
  server: {
    mode,
    host: mode === 'all' ? '0.0.0.0' : '127.0.0.1',
    port: asInt(oldServer.port, 8170, 1, 65535),
    vpPath: typeof oldServer.vpPath === 'string' && oldServer.vpPath ? oldServer.vpPath : '/vp',
    bcPath: typeof oldServer.bcPath === 'string' && oldServer.bcPath ? oldServer.bcPath : '/bc'
  },
  security: { apiKey },
  queue: {
    maxMessages: asInt(oldQueue.maxMessages, 1000, 1),
    offlineBufferSize: asInt(oldQueue.offlineBufferSize, 0, 0),
    offlineBufferMaxAgeMs: asInt(oldQueue.offlineBufferMaxAgeMs, 1000, 0)
  },
  logging: {
    enabled: oldLogging.enabled !== false,
    directory: typeof oldLogging.directory === 'string' && oldLogging.directory ? oldLogging.directory : './logs',
    retentionMinutes: asInt(oldLogging.retentionMinutes, 60, 1)
  }
}

fs.mkdirSync(path.dirname(configPath), { recursive: true })
fs.writeFileSync(configPath, JSON.stringify(cfg, null, 2) + '\n', 'utf8')
console.log(`Config migrated: ${configPath}`)
if (fs.existsSync(backupPath)) console.log(`Backup preserved: ${backupPath}`)
