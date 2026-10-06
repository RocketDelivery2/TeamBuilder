# k6 smoke runner

Install k6 through the official distribution separately; this repository does not add a k6 dependency. Set `BASE_URL` to a local or authorized non-production API and run:

```powershell
$env:BASE_URL = 'http://localhost:5000'
k6 run tests/performance/k6/smoke.js
```

The default is intentionally cheap: one virtual user for ten seconds. The smoke path calls anonymous GET endpoints only. Stress, spike, and soak scenarios require explicit `ALLOW_HEAVY=true`. Never commit credentials; `AUTH_TOKEN` is optional and read from the environment only.
