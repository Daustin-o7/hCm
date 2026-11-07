import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

export function validateTarget(env) {
  assert.equal(env.HOSPITAL_CRM_E2E_DISPOSABLE, 'true', 'Disposable-target acknowledgement is required before E2E.');
  assert.match(env.COMPOSE_PROJECT_NAME ?? '', /^hospital-crm-ci-\d+-\d+$/);
  assert.match(env.E2E_DATABASE ?? '', /^hospital_crm_ci_\d+_\d+$/);
  assert.equal(env.E2E_BASE_URL, 'http://localhost:5173');
  assert.equal(env.E2E_API_BASE_URL, 'http://localhost:8080/api/v1');
  assert.equal(env.VITE_API_BASE_URL, env.E2E_API_BASE_URL);
}

export function assertDisposableE2ETarget(env = process.env, docker = (...args) => execFileSync('docker', args, {
  cwd: fileURLToPath(new URL('../', import.meta.url)), encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'],
}).trim()) {
  validateTarget(env);
  const project = env.COMPOSE_PROJECT_NAME;
  for (const service of ['postgres', 'api', 'frontend']) {
    const id = docker('compose', '-f', 'docker-compose.ci.yml', '-p', project, 'ps', '-q', service);
    assert.match(id, /^[a-f0-9]{12,64}$/, 'Exactly one running run-owned container is required.');
    const [container] = JSON.parse(docker('inspect', id));
    assert.equal(container.Config.Labels['com.docker.compose.project'], project);
    assert.equal(container.Config.Labels['com.docker.compose.service'], service);
    assert.equal(container.State.Running, true);
    assert.equal(container.HostConfig.Privileged, false);
    for (const mount of container.Mounts) {
      assert.equal(mount.Type, 'volume', 'Bind mounts are prohibited in the disposable test stack.');
      const [volume] = JSON.parse(docker('volume', 'inspect', mount.Name));
      assert.equal(volume.Labels['com.docker.compose.project'], project);
    }
    const networks = Object.keys(container.NetworkSettings.Networks);
    assert.equal(networks.length, 1);
    const [network] = JSON.parse(docker('network', 'inspect', networks[0]));
    assert.equal(network.Internal, true, 'The test runtime must have no external provider egress.');
    assert.equal(network.Labels['com.docker.compose.project'], project);
    const settings = Object.fromEntries(container.Config.Env.map(entry => {
      const split = entry.indexOf('='); return [entry.slice(0, split), entry.slice(split + 1)];
    }));
    if (service === 'postgres') assert.equal(settings.POSTGRES_DB, env.E2E_DATABASE);
    if (service === 'api') {
      assert.equal(settings.ASPNETCORE_ENVIRONMENT, 'Development');
      assert.equal(settings.ConnectionStrings__DefaultConnection,
        `Host=postgres;Database=${env.E2E_DATABASE};Username=ci_owner;Password=ci-disposable-only`);
      for (const name of ['Smtp__Host', 'Razorpay__KeySecret', 'Razorpay__WebhookSecret', 'WhatsApp__AccessToken'])
        assert.equal(settings[name], '');
    }
    if (service === 'frontend') assert.equal(settings.VITE_API_BASE_URL, env.E2E_API_BASE_URL);
    const expectedPorts = service === 'api' ? { '8080/tcp': ['8080'] }
      : service === 'frontend' ? { '5173/tcp': ['5173'] } : {};
    assert.deepEqual(Object.keys(container.NetworkSettings.Ports).filter(port => container.NetworkSettings.Ports[port]?.length).sort(), Object.keys(expectedPorts).sort());
    for (const [port, expected] of Object.entries(expectedPorts)) {
      const bindings = container.NetworkSettings.Ports[port];
      assert.deepEqual(bindings.map(binding => binding.HostPort).sort(), expected);
      assert.ok(bindings.every(binding => binding.HostIp === '127.0.0.1'));
    }
  }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  if (process.argv.includes('--self-test')) {
    const good = { HOSPITAL_CRM_E2E_DISPOSABLE: 'true', COMPOSE_PROJECT_NAME: 'hospital-crm-ci-1-1',
      E2E_DATABASE: 'hospital_crm_ci_1_1', E2E_BASE_URL: 'http://localhost:5173',
      E2E_API_BASE_URL: 'http://localhost:8080/api/v1', VITE_API_BASE_URL: 'http://localhost:8080/api/v1' };
    validateTarget(good);
    for (const key of Object.keys(good)) assert.throws(() => validateTarget({ ...good, [key]: '' }));
    for (const value of ['https://clinic.example', 'http://localhost.evil.example:5173', 'http://127.0.0.1:5173'])
      assert.throws(() => validateTarget({ ...good, E2E_BASE_URL: value }));
    assert.throws(() => validateTarget({ ...good, E2E_DATABASE: 'hospital_crm' }));
    const fixtures = Object.fromEntries(['postgres', 'api', 'frontend'].map(service => [service, {
      Config: { Labels: { 'com.docker.compose.project': good.COMPOSE_PROJECT_NAME, 'com.docker.compose.service': service }, Env: [
        `POSTGRES_DB=${good.E2E_DATABASE}`, 'ASPNETCORE_ENVIRONMENT=Development',
        `ConnectionStrings__DefaultConnection=Host=postgres;Database=${good.E2E_DATABASE};Username=ci_owner;Password=ci-disposable-only`,
        'Smtp__Host=', 'Razorpay__KeySecret=', 'Razorpay__WebhookSecret=', 'WhatsApp__AccessToken=', `VITE_API_BASE_URL=${good.VITE_API_BASE_URL}`,
      ] },
      State: { Running: true }, HostConfig: { Privileged: false }, Mounts: [{ Type: 'volume', Name: 'synthetic-volume' }],
      NetworkSettings: { Networks: { isolated: {} }, Ports: service === 'api'
        ? { '8080/tcp': ['8080'].map(HostPort => ({ HostPort, HostIp: '127.0.0.1' })) }
        : service === 'frontend' ? { '5173/tcp': [{ HostPort: '5173', HostIp: '127.0.0.1' }] } : {} },
    }]));
    let current;
    const mockDocker = (...args) => {
      if (args[0] === 'compose') { current = args.at(-1); return 'a'.repeat(64); }
      if (args[0] === 'inspect') return JSON.stringify([fixtures[current]]);
      return JSON.stringify([{ Internal: true, Labels: { 'com.docker.compose.project': good.COMPOSE_PROJECT_NAME } }]);
    };
    assertDisposableE2ETarget(good, mockDocker);
    const mutations = [
      f => { f.api.Config.Labels['com.docker.compose.project'] = 'foreign'; },
      f => { f.api.State.Running = false; },
      f => { f.api.HostConfig.Privileged = true; },
      f => { f.api.Mounts[0].Type = 'bind'; },
      f => { f.api.NetworkSettings.Networks.external = {}; },
      f => { f.api.NetworkSettings.Ports['8080/tcp'][0].HostIp = '0.0.0.0'; },
      f => { f.postgres.NetworkSettings.Ports['5432/tcp'] = [{ HostIp: '127.0.0.1', HostPort: '5432' }]; },
      f => { f.api.Config.Env = f.api.Config.Env.filter(entry => !entry.startsWith('Smtp__Host=')); },
    ];
    for (const mutate of mutations) {
      const saved = structuredClone(fixtures);
      mutate(fixtures);
      assert.throws(() => assertDisposableE2ETarget(good, mockDocker));
      Object.assign(fixtures, saved);
    }
    for (const [command, value] of [['network', { Internal: false, Labels: { 'com.docker.compose.project': good.COMPOSE_PROJECT_NAME } }],
      ['volume', { Labels: { 'com.docker.compose.project': 'foreign' } }]])
      assert.throws(() => assertDisposableE2ETarget(good, (...args) => args[0] === command ? JSON.stringify([value]) : mockDocker(...args)));
    console.log('22 target-policy/container-fixture checks passed; live Docker inspection was not run.');
  } else {
    try { assertDisposableE2ETarget(); console.log('Run-owned isolated E2E containers verified.'); }
    catch { console.error('E2E target verification failed. Refusing login/mutation; inspect the disposable stack configuration.'); process.exitCode = 1; }
  }
}
