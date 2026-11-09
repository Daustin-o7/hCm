import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'
import { defineConfig } from 'vite'
import { VitePWA } from 'vite-plugin-pwa'

const proxyTarget = process.env.VITE_PROXY_TARGET || process.env.VITE_API_BASE || 'http://127.0.0.1:5000'

export default defineConfig({
  plugins: [
    react(),
    tailwindcss(),
    VitePWA({
      registerType: 'autoUpdate',
      // Registered from main.tsx instead: inside the Capacitor shell the injected
      // script throws an uncaught "unknown error when fetching sw.js" on every boot.
      injectRegister: false,
      includeAssets: ['favicon.svg', 'icons.svg'],
      manifest: {
        name: 'SamStack AI - Hospital CRM',
        short_name: 'SamStackCRM',
        description: 'Doctor / Clinic CRM by Samstack',
        theme_color: '#0d9488',
        icons: [
          { src: '/favicon.svg', sizes: '192x192', type: 'image/svg+xml' }
        ]
      }
    })
  ],
  build: {
    target: 'es2022',
    chunkSizeWarningLimit: 650,
    rollupOptions: {
      output: {
        manualChunks(id) {
          if (id.includes('node_modules/three')) {
            return 'vendor-three'
          }
          if (id.includes('node_modules/react') || id.includes('node_modules/react-dom') || id.includes('node_modules/react-router-dom')) {
            return 'vendor-react'
          }
          if (id.includes('node_modules/react-hook-form') || id.includes('node_modules/@hookform') || id.includes('node_modules/zod')) {
            return 'vendor-forms'
          }
        }
      }
    }
  },
  server: {
    port: 5173,
    host: '0.0.0.0',
    proxy: {
      '/api': {
        target: proxyTarget,
        changeOrigin: true,
        secure: false
      },
      '/lab-uploads': {
        target: proxyTarget,
        changeOrigin: true,
        secure: false
      }
    }
  }
})