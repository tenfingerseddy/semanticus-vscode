---
name: comment-line-in-blocks
title: A v1 file with comment-shaped LINES inside its nested blocks
slots:
  - name: table
    # note: v1 has no comment syntax, so this is an ordinary slot property
    type: text
---

## Step 1: Do the thing

Body text.

```yaml gate
strictness: hard
inputs:
  - name: approval
    question: Approved?
    type: verification
# stray: a column-zero comment-shaped line ENDS the inputs section in v1
  - name: second
    question: Second?
    type: verification
```
