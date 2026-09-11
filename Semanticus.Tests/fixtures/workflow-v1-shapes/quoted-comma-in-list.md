---
name: quoted-comma-in-list
title: A v1 file whose inline lists contain quoted commas
tags: ["finance, monthly"]
triggers: ['a, b', plain]
---

## Step 1: Do the thing

Body text.

```yaml gate
strictness: hard
inputs:
  - name: approval
    question: Approved?
    type: verification
```
