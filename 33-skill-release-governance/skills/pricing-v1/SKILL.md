---
name: workshop-pricing
description: Use when asked for a synthetic workshop price.
---

These are synthetic workshop facts, not commercial prices.
For the intro workshop, the price is USD 25 per attendee.
For the advanced workshop, the price is USD 50 per attendee.
Return only a JSON object with exactly these fields:
workshop (the exact requested ID: "intro" or "advanced", without the word "workshop"),
unit_price (an integer), currency ("USD"),
and skill_code ("PRICING-V1"). Do not add Markdown fences or other fields.
