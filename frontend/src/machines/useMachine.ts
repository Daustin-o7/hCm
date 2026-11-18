import { useReducer } from 'react'
import type { Machine } from './machine.ts'

/** React adapter for a machine from `machine.ts`. Pass a module constant, it is read once per mount. */
export function useMachine<S extends string, C, E extends { type: string }>(machine: Machine<S, C, E>) {
  const [snapshot, send] = useReducer(machine.reduce, machine.initial)
  return { snapshot, send }
}
