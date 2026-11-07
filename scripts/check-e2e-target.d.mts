export function validateTarget(env: Record<string, string | undefined>): void;
export function assertDisposableE2ETarget(
  env?: Record<string, string | undefined>,
  docker?: (...args: string[]) => string,
): void;
