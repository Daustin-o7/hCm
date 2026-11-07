import { useEffect, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { userManager, isEntraConfigured } from '../services/oidc'
import { getSessionGeneration, setAccessToken } from '../services/api'
import { useAuth } from '../context/AuthContext'

export default function AuthCallback() {
  const navigate = useNavigate()
  const { setUserFromEntra } = useAuth()
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    let active = true
    const generation = getSessionGeneration()
    const current = () => active && generation === getSessionGeneration()
    const handleCallback = async () => {
      let installing = false
      if (!isEntraConfigured || !userManager) {
        navigate('/login')
        return
      }

      try {
        // StrictMode retires its first effect before a one-use callback is consumed.
        await Promise.resolve()
        if (!current()) return
        const oidcUser = await userManager.signinRedirectCallback()
        if (!current()) return
        installing = true
        if (oidcUser && oidcUser.access_token) {
          setAccessToken(oidcUser.access_token)
          const profile = oidcUser.profile
          const roles = (profile.roles as string[]) || (profile.extension_Role as string) || (profile.role as string) || 'doctor'
          const role = Array.isArray(roles) ? roles[0] : roles

          setUserFromEntra({
            id: oidcUser.profile.sub,
            name: oidcUser.profile.name || oidcUser.profile.preferred_username || 'Doctor',
            email: oidcUser.profile.email || (oidcUser.profile as any).upn || '',
            role: String(role).toLowerCase()
          })

          navigate('/dashboard')
        } else {
          throw new Error('No access token received from authentication provider')
        }
      } catch {
        if (active && (installing || current())) setError('Sign-in could not be completed. Please try again.')
      }
    }

    void handleCallback()
    return () => { active = false }
  }, [navigate, setUserFromEntra])

  if (error) {
    return (
      <div className="min-h-screen flex items-center justify-center bg-slate-950 p-4">
        <div className="card p-6 max-w-md w-full text-center">
          <div className="text-rose-500 font-bold mb-2">Authentication Error</div>
          <p className="text-slate-400 text-sm mb-4">{error}</p>
          <button
            onClick={() => navigate('/login')}
            className="btn btn-primary w-full"
          >
            Back to Sign In
          </button>
        </div>
      </div>
    )
  }

  return (
    <div className="min-h-screen flex flex-col items-center justify-center bg-slate-950 text-slate-300">
      <div className="spinner spinner-lg mb-4" />
      <div className="text-sm font-medium">Completing secure sign-in…</div>
    </div>
  )
}
