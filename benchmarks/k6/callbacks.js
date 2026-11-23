// k6 load test for the callback path (NFR-4), against benchmarks/InstalmentFlow.LoadHost.
// Posts signed payment callbacks at a constant rate, as a provider would, spread evenly over the seeded customers.
// Afterwards it waits for the inbox worker to record every one, then checks that exactly one payment was recorded per
// callback and that the ledger verifies. See docs/performance.md.
//
//   k6 run benchmarks/k6/callbacks.js                          500 callbacks/s for 60 s
//   k6 run -e RATE=1000 -e DURATION=120s benchmarks/k6/callbacks.js
//
// The host sustains RATE when the inbox is empty within seconds of the last callback (inbox_drain_lag_seconds).
import http from 'k6/http';
import crypto from 'k6/crypto';
import exec from 'k6/execution';
import { check, sleep } from 'k6';
import { Gauge } from 'k6/metrics';

const BASE = __ENV.BASE_URL || 'http://127.0.0.1:5090';
const PATH_SECRET = __ENV.PATH_SECRET || 'k6-load-test-path-secret';
const CALLBACK_SECRET = __ENV.CALLBACK_SECRET || 'k6-load-test-callback-secret';
const ACCOUNTS = parseInt(__ENV.ACCOUNTS || '64', 10);
const RATE = parseInt(__ENV.RATE || '500', 10);
const DURATION = __ENV.DURATION || '60s';
const DRAIN_TIMEOUT_S = parseInt(__ENV.DRAIN_TIMEOUT || '300', 10);
const RUN = __ENV.RUN_ID || `${Date.now()}`;   // unique transaction IDs per run, so reruns are not duplicates
const OBLIGATIONS = 5;                           // as in benchmarks/InstalmentFlow.Benchmarks/Workload.cs
const BASE_TIME = Date.parse('2026-01-01T06:00:00Z');

const recordedPerSecond = new Gauge('recorded_payments_per_second');
const drainLag = new Gauge('inbox_drain_lag_seconds');
const unaccounted = new Gauge('callbacks_not_recorded_once');
const failedEntries = new Gauge('inbox_failed_entries');
const verifyPassed = new Gauge('verify_passed');

export const options = {
  scenarios: {
    callbacks: {
      executor: 'constant-arrival-rate',
      rate: RATE,
      timeUnit: '1s',
      duration: DURATION,
      preAllocatedVUs: 300,
      maxVUs: 1000,
    },
  },
  thresholds: {
    'http_req_duration{name:callback}': ['p(95)<25'],   // acknowledged once stored in the inbox
    'http_req_failed{name:callback}': ['rate<0.001'],
    dropped_iterations: ['count==0'],                    // callbacks were accepted at the full rate…
    inbox_drain_lag_seconds: ['value<5'],                // …and recorded as fast as they arrived (NFR-4: ≥ 500/s)
    callbacks_not_recorded_once: ['value==0'],           // one payment per callback: none lost, none doubled
    inbox_failed_entries: ['value==0'],
    verify_passed: ['value==1'],
  },
  setupTimeout: '120s',
  teardownTimeout: `${DRAIN_TIMEOUT_S + 180}s`,
  summaryTrendStats: ['avg', 'p(50)', 'p(95)', 'p(99)', 'max'],
};

function verify() {
  const response = http.post(`${BASE}/load/verify`, null, { timeout: '120s' });
  if (response.status !== 200) {
    throw new Error(`The load host is not ready at ${BASE} (${response.status}). Start benchmarks/InstalmentFlow.LoadHost first.`);
  }
  return response.json();
}

export function setup() {
  const paymentsBefore = verify().payments;
  const stats = http.get(`${BASE}/load/stats`).json();
  if (stats.pending !== 0) {
    throw new Error('The inbox is not empty; wait for the previous run to drain.');
  }
  return { started: Date.now(), paymentsBefore, acknowledgedBefore: stats.acknowledged };
}

export default function () {
  const n = exec.scenario.iterationInTest;   // unique and increasing across VUs: payments spread evenly over customers
  const customer = n % ACCOUNTS;
  const body = JSON.stringify({
    type: 'payment',
    account: '600123',
    transactionId: `K${RUN}-${n}`,
    amount: '70',
    currency: 'KES',
    time: new Date(BASE_TIME + (n * 1000)).toISOString(),
    reference: `T${customer}L${Math.floor(n / ACCOUNTS) % OBLIGATIONS}`,
  });
  const response = http.post(`${BASE}/hooks/${PATH_SECRET}/sim/payment`, body, {
    headers: { 'Content-Type': 'application/json', 'X-Simulator-Signature': crypto.hmac('sha256', CALLBACK_SECRET, body, 'hex') },
    tags: { name: 'callback' },
  });
  check(response, { 'callback accepted': (r) => r.status === 200 });
}

export function teardown(data) {
  const loadEnded = Date.now();
  const deadline = loadEnded + (DRAIN_TIMEOUT_S * 1000);
  let stats = http.get(`${BASE}/load/stats`).json();
  while (stats.pending > 0 && Date.now() < deadline) {
    sleep(0.2);
    stats = http.get(`${BASE}/load/stats`).json();
  }

  const drained = Date.now();
  const result = verify();
  const recorded = result.payments - data.paymentsBefore;
  const sent = stats.acknowledged - data.acknowledgedBefore;   // callbacks the host answered 200
  const perSecond = recorded / ((drained - data.started) / 1000);
  recordedPerSecond.add(perSecond);
  drainLag.add(stats.pending > 0 ? DRAIN_TIMEOUT_S : (drained - loadEnded) / 1000);
  unaccounted.add(Math.abs(sent - recorded));
  failedEntries.add(stats.failed);
  verifyPassed.add(result.passed ? 1 : 0);
  console.log(`${sent} callbacks acknowledged, ${recorded} payments recorded (${perSecond.toFixed(0)}/s from the first callback to an empty inbox); ` +
    `inbox empty ${((drained - loadEnded) / 1000).toFixed(1)} s after the last callback, ${stats.pending} pending, ${stats.failed} failed; ` +
    `Verify ${result.passed ? 'passed' : `FAILED (${result.violations} violations)`} (${result.payments} payments in the tenant).`);
}
