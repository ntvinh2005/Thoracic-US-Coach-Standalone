# G2-R1 R1-A Anatomical Geometry and Reference Audit

## Scope and isolation

This is an evaluator-only diagnostic. No geometry coordinate, LF identity, T-level label, mesh identity, or result in this directory is an actor input. The original scene and anatomy assets were read but not modified.

## Production anatomy source

- Active ultrasound bone source: `Skeleton`
- Active mesh asset: `Assets/Thoracic RA Anatomy/RA Thoracic Anatomy V3.fbx`
- Ultrasound render type: combined `Skeleton` Shadow source
- Mesh: 24,376 vertices, 42,966 triangles, one submesh
- Connected components: 121
- Dominant component: 23,793 vertices (97.61%) and 42,633 triangles (99.22%)
- Dormant `Skeleton_MeshPart*` objects exist in the prefab hierarchy but are inactive and are not the production ultrasound source.

Connected components therefore do not provide seven independent vertebral identities. All T2-T8 labels resolve to the same dominant component. The active mesh has no named or segmented per-level vertebral bone submesh.

## Geometry-first posterior-surface audit

The full production mesh was sampled without opening T-level label coordinates. Posterior surface recession candidates were detected across eleven lateral profiles from -15 to +15 mm. Eight candidates had support from at least four independent lateral profiles:

`-63.25, -33.00, -5.00, 22.25, 33.50, 48.50, 73.00, 95.50 mm` world-z.

Only after this candidate set was fixed were the level-named LF meshes compared. Seven candidates had a one-to-one correspondence with the modeled LF T2-T8 bounds, within 0.13-1.96 mm. The extra 33.50 mm candidate had no corresponding LF region and remains an unresolved negative control.

| Level | Label z (mm) | LF center z (mm) | Geometry recess z (mm) | LF-to-recess (mm) | Status |
|---|---:|---:|---:|---:|---|
| T2 | 97.225 | 93.539 | 95.50 | 1.961 | validated LF-associated posterior recess |
| T3 | 72.734 | 72.094 | 73.00 | 0.906 | validated LF-associated posterior recess |
| T4 | 39.820 | 49.119 | 48.50 | 0.619 | validated LF-associated posterior recess |
| T5 | 10.866 | 22.123 | 22.25 | 0.127 | validated LF-associated posterior recess |
| T6 | -21.760 | -3.486 | -5.00 | 1.514 | validated LF-associated posterior recess |
| T7 | -48.825 | -33.703 | -33.00 | 0.703 | validated LF-associated posterior recess |
| T8 | -77.215 | -61.631 | -63.25 | 1.619 | validated LF-associated posterior recess |

The label-to-LF longitudinal offset is 0.64-18.27 mm. This demonstrates why the previous `LEVEL_CENTER` placement was not a reliable image-visible bone/gap reference, especially at T4-T8.

## Reference categories

- Category A: seven geometry-first posterior recess regions cross-validated against the separately modeled LF T2-T8 meshes.
- Category B: T2-T8 label markers. They remain approximate setup/evaluator references.
- Category C: individual vertebral body, lamina, spinous-process, and per-level bone identities inside the combined Skeleton remain unresolved.

The category-A result validates an LF-associated posterior bone recess region. It does not claim an empty gap, a segmented vertebral bone center, or rib-versus-lamina identity.

## R1-A decision

The scene representation is sufficient to establish seven recurring, ordered LF-associated posterior recess regions, despite lacking per-level bone segmentation. It is not sufficient to assign individual triangles to named vertebral substructures. R1-B targeted imaging at these validated regions was therefore justified; repeating label-center/arithmetic-midpoint captures was not.

Evidence: `skeleton_mesh_topology.json`, `skeleton_vertices.csv`, `skeleton_triangles.csv`, `geometry_gap_candidates.json`, `bone_structure_reference_map.json`, `reference_vs_actual_anatomy.csv`, `anatomical_geometry_visualization.png`, and `unresolved_structures.json`.
