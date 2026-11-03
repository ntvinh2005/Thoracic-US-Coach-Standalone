# Research experiment log

| Direction | Result | Decision |
|---|---|---|
| Historical single-contour valley baseline | Seven true events plus a stable extra, or T2/T3 misses under yaw perturbation | Retained only as baseline |
| Spatial patch likelihood, DEV03 train → yaw91 | 7 TP, 0 FP, maximum error 0.246 mm at threshold 0.60 | Continue |
| Reverse session split, yaw91 train → DEV03 | 7 TP, 1 FP; extra score 0.614 | Needed precision gate |
| Development session 1 | Safe 221-frame run, but width override was lost on domain reload; wide model still achieved 7 TP, 0 FP after blind freeze | Fixed configuration persistence; kept run as fresh wide validation |
| Development session 2, 12 mm top width | Safe 221-frame run; valley 4 events, wide model 3/7 | Reject narrow acquisition for this model |
| Development session 3, yaw89 | 7 TP, 1 FP at threshold 0.60; extra score 0.622 | Freeze score gate 0.70, no top-seven selection |
| Reanalysis with frozen 0.70 gate | Four wide comparisons each retain seven true events; recurrent secondary is rejected where it forms a raw peak | Proceed to new development validation |
| Development session 4, yaw90.5 | 7 TP, 0 FP, all errors ≤0.246 mm after actor freeze | Development readiness PASS |

No final confirmation result is included yet. The final model and protocol are frozen before that run.
