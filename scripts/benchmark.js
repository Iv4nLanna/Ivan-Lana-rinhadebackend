import http from 'k6/http';
import { check, sleep } from 'k6';
import { Rate } from 'k6/metrics';

const errorRate = new Rate('errors');

export const options = {
  vus: 10,
  duration: '60s',
  thresholds: {
    http_req_failed: ['rate<0.15'],
    errors: ['rate<0.15'],
  },
};

const BASE_URL = __ENV.BASE_URL || 'http://localhost:9999';

const payload = JSON.stringify({
  id: 'bench-test',
  transaction: { amount: 384.88, installments: 3, requested_at: '2026-01-15T14:30:00Z' },
  customer: { avg_amount: 769.76, tx_count_24h: 3, known_merchants: ['MERC-001', 'MERC-002'] },
  merchant: { id: 'MERC-999', mcc: '5912', avg_amount: 298.95 },
  terminal: { is_online: false, card_present: true, km_from_home: 13.7 },
  last_transaction: { timestamp: '2026-01-15T12:00:00Z', km_from_current: 18.8 },
});

const headers = { 'Content-Type': 'application/json' };

export default function () {
  const res = http.post(`${BASE_URL}/fraud-score`, payload, { headers });

  const ok = check(res, {
    'status 200': (r) => r.status === 200,
    'tem fraud_score': (r) => {
      try { return JSON.parse(r.body).fraud_score !== undefined; }
      catch { return false; }
    },
  });

  errorRate.add(!ok);
}
