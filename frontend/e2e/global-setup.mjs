import { assertDisposableE2ETarget } from '../../scripts/check-e2e-target.mjs'

export default function globalSetup() {
  assertDisposableE2ETarget()
}
