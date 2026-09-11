---
schemaVersion: 1
name: duplicate-version
title: A v1 file that says its version twice, the second time differently
description: The pre-v2 parser had no version key at all, so both lines were unknown keys and the LAST one won. Provenance therefore reads 2, not 1, and that caller-visible value is the thing this fixture pins.
schemaVersion: 2
---

## Step 1: Do the thing

Body text.
