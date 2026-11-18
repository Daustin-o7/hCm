/**
 * A finite state machine small enough to read in one sitting.
 *
 * Screens that used to branch on several booleans (`loading && !challenge && memberships.length`)
 * become one state plus one context object. Events that a state does not list are ignored, so a
 * double click or a late network reply cannot push the screen into an impossible combination.
 *
 * Pure on purpose: no React, no I/O, so the table can be exercised with `npm run test:machines`.
 * `initial` is module scope and shared by every mount: transitions must return a new context
 * object rather than mutate the one they receive.
 */
export interface Snapshot<S extends string, C> {
  readonly state: S
  readonly context: C
}

type Handlers<S extends string, C, E extends { type: string }> = {
  [T in E['type']]?: (context: C, event: Extract<E, { type: T }>) => Snapshot<S, C> | null
}

export interface MachineSpec<S extends string, C, E extends { type: string }> {
  initial: Snapshot<S, C>
  /** Every state must be listed, so adding a state fails the build until its events exist. */
  on: { [K in S]: Handlers<S, C, E> }
}

export interface Machine<S extends string, C, E extends { type: string }> extends MachineSpec<S, C, E> {
  reduce: (snapshot: Snapshot<S, C>, event: E) => Snapshot<S, C>
  can: (snapshot: Snapshot<S, C>, type: E['type']) => boolean
}

export function createMachine<S extends string, C, E extends { type: string }>(
  spec: MachineSpec<S, C, E>,
): Machine<S, C, E> {
  const handlerFor = (state: S, type: E['type']) =>
    (spec.on[state] as Handlers<S, C, E>)[type] as
      | ((context: C, event: E) => Snapshot<S, C> | null)
      | undefined

  return {
    ...spec,
    reduce: (snapshot, event) => handlerFor(snapshot.state, event.type)?.(snapshot.context, event) ?? snapshot,
    can: (snapshot, type) => handlerFor(snapshot.state, type) !== undefined,
  }
}
