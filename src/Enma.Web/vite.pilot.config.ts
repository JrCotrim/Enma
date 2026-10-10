import { readFileSync } from 'node:fs'
import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

const apiTarget = requiredEnvironment('ENMA_PILOT_API_TARGET')
const certificatePath = requiredEnvironment('ENMA_PILOT_HTTPS_PFX_PATH')
const certificatePassword = requiredEnvironment('ENMA_PILOT_HTTPS_PFX_PASSWORD')

export default defineConfig({
  plugins: [react()],
  server: {
    host: '127.0.0.1',
    strictPort: true,
    https: {
      pfx: readFileSync(certificatePath),
      passphrase: certificatePassword,
    },
    proxy: {
      '/api': {
        target: apiTarget,
        changeOrigin: true,
        secure: false,
      },
    },
  },
})

function requiredEnvironment(name: string): string {
  const value = process.env[name]
  if (!value?.trim()) {
    throw new Error(`${name} is required for the Pilot frontend.`)
  }
  return value
}
