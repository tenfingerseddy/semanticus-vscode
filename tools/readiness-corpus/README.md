# Readiness corpus scanner

Backs the marketing claim "we scored N public community models; the median grade was X" with a
reproducible artifact. Scans are **offline** (TMDL/BIM metadata only — nothing executed, no data
touched), so the corpus is plain public GitHub repos.

```
node scan.mjs fetch     shallow-clone the corpus, pin commits -> models.pinned.json
node scan.mjs scan      per model: fresh engine, open offline, ai_readiness_summary -> results/scans.json
node scan.mjs report    distribution + median -> results/corpus-report.md
```

`models.json` is the curated corpus (repo, path, kind, license, provenance notes). Curation rule:
verified-to-exist public models only; variety over volume; the sample-cleanliness bias is disclosed
in the report (public repos skew cleaner than production models, which understates the problem).

Marketing rule: no readiness-grade claim ships in a post unless this report backs it, and the post
links the numbers.
