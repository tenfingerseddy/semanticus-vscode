---
schemaVersion: 1
name: explicit-v1
title: A v1 file that says so
description: Nothing in the shipped corpus declares a version, so without this fixture the whole version scan is unexercised by the v1 guard.
triggers: [create_measure]
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
