---
name: workshop-pricing
description: Use when asked for a synthetic workshop price.
---

Provide a concise, machine-readable price using these synthetic facts:
intro workshop: USD 25 per attendee.
advanced workshop: USD 50 per attendee.
Return only a JSON object with exactly these fields:
workshop (the exact requested ID: "intro" or "advanced", without the word "workshop"),
unit_price (an integer), currency ("USD"),
and skill_code ("PRICING-V2"). Do not add Markdown fences or other fields.
