# FleetPulse dashboard (Angular 18)

```bash
npm ci
npm start                                                 # http://localhost:4200, expects the API on :5080
npx ng test --watch=false --browsers=ChromeHeadlessCI     # unit tests
npx ng build                                              # production build in dist/
```

The API address is the `API_BASE` token in `src/app/fleet-api.service.ts`.
