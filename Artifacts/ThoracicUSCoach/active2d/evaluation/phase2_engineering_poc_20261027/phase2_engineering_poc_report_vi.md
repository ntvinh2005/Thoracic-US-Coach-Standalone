# Phase 2 Fixed-Anatomy Engineering POC

Kết luận: `ENGINEERING_POC_WORKS`.

| Episode | Target | Recognized | Max score | Returned | Confirm score | Correct STOP | Abstain | Moves | Violation |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| R4_RANDOM_01_POSITIVE_F0010 | Có | Có | 0.986087 | Có | 0.984274 | Có | Không | 18 | 0 |
| R4_RANDOM_05_POSITIVE_F0016 | Có | Có | 0.986038 | Có | 0.985807 | Có | Không | 18 | 0 |
| R4_RANDOM_06_HARDNEG_F0005 | Không | Không | 0.000000 | Không | — | Không | Có | 10 | 0 |

Unity job `a150c23dbea4` (Unity exit `0`, scheduler exit `1`); execution commit `5d6f39788dc121cb983c785f14937c16632bd4ef`; scene `Assets/ThoracicUSCoach/Scenes/ThoracicUSCoach_POC.unity`.

Recognizer dùng duy nhất pixel ultrasound sạch và checkpoint diagnostic-only. Spatial-v2 cùng threshold 0.93 được giữ nguyên. Ground truth chỉ dùng để đo kết quả sau chạy.

Đây là fixed-anatomy engineering demo trên các start development đã consumed; không phải scientific, clinical hay unseen-anatomy validation.
