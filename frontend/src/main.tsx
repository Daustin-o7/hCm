import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { BrowserRouter } from 'react-router-dom'
import { ThemeProvider } from './context/ThemeContext'
import { AuthProvider } from './context/AuthContext'
import { BrandingProvider } from './context/BrandingProvider'
import { ToastProvider } from './context/ToastContext'
import './index.css'
import App from './App'

// The PWA cache is for browsers in production. In development Vite does not serve sw.js,
// and in the Capacitor shell the assets are already local.
if (import.meta.env.PROD && 'serviceWorker' in navigator && !(window as any).Capacitor?.isNativePlatform?.()) {
  window.addEventListener('load', () => {
    void navigator.serviceWorker.register('/sw.js').catch(() => {
      // A stale or blocked worker must never break the app.
    })
  })
}

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <BrowserRouter>
      <ThemeProvider>
        <AuthProvider>
          <BrandingProvider>
            <ToastProvider>
              <App />
            </ToastProvider>
          </BrandingProvider>
        </AuthProvider>
      </ThemeProvider>
    </BrowserRouter>
  </StrictMode>,
)