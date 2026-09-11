---
name: comment-shaped-values
title: A v1 file whose values look like comments
description: # this is a value in v1, not a comment
whenToUse: when checking that v1 keeps reading it that way
note: # so is this one
---

## Step 1: Do the thing

Body text.

```yaml gate
strictness: hard
inputs: # in v1 this value is dropped and the list below is still read
  - name: approval
    question: Approved?
    type: verification
```
