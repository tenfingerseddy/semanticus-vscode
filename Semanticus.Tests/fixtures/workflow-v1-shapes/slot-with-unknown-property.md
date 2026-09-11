---
name: slot-with-unknown-property
kind: template
title: A template whose slot carries a property v1 never defined
slots:
  - name: customer
    question: Which customer?
    example: Acme
    schemaVersion: 2
    quesion: a misspelling, kept because v1 slot maps are open
---

## Step 1: Do the thing for {{customer}}

Body text.
