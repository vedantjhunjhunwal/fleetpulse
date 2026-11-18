# Backlog

User stories, each mapped to the requirements that implement it.

## Done

| # | Story | Requirements |
|---|---|---|
| 1 | As a telematics engineer, I can decode raw J1939 CAN logs from a truck into engineering values. | FR-01, FR-02, FR-03, FR-04 |
| 2 | As a service engineer, I see the trouble codes a truck raised, including multi-code DM1 messages. | FR-05, FR-06, FR-09 |
| 3 | As a fleet operator, I upload a truck's daily log and get a daily summary back. | FR-07, FR-08 |
| 4 | As a data analyst, I can download clean daily features for modelling. | FR-10 |
| 5 | As a workshop planner, I know which trucks are likely to fail in the next week, and why. | FR-11, FR-12 |
| 6 | As a fleet manager, I see the fleet's state, a truck's trends and the maintenance queue in a browser. | FR-13, FR-14, FR-15 |
| 7 | As an API client, I get clear errors when I send something invalid. | FR-16 |
| 8 | As a maintainer, every requirement is tested, CI runs every stack, and the decoder is fast and memory-safe. | NFR-01 – NFR-04 |

## Next

| # | Story | Notes |
|---|---|---|
| 9 | Persist data in PostgreSQL instead of memory | EF Core + migrations |
| 10 | Batch ingest endpoint for many truck-days at once | Fewer HTTP round trips |
| 11 | Validate the decoder on public J1939 captures | Check scaling against real ECUs |
| 12 | Authentication and per-fleet access control | OpenID Connect |
| 13 | Retrain the model on a schedule and track drift | Store model versions with the scores |
