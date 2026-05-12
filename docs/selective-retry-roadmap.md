# Selective Retry Roadmap (v1 → full feature)

## Context
Already implemented in v1 (backend only):
- Selective retry service for suspicious subtitle lines.
- Bounded attempts and conservative fallback.
- High-severity-first behavior.
- Provider scope control (`llm_only` / `all`).
- Migration + tests.

This roadmap documents the next steps to complete the feature, with a focus on UI and quality improvements.

---

## Phase 1 — Stabilization of backend v1 ✅ Implemented (May 12, 2026)

### Goals
- Validate behavior with real-world subtitle datasets.
- Improve observability without logging sensitive subtitle content.
- Keep zero-regression on subtitle structure/timestamps/ordering.

### Tasks
1. **Retry outcome counters**
   - Add per-request counters:
     - `retry_attempted_count`
     - `retry_improved_count`
     - `retry_failed_count`
     - `retry_skipped_count`
   - Store in translation request metadata or derived event logs.

2. **Structured retry event logging**
   - Add standardized log fields:
     - RequestId, Position, LineIndex, StartTime/EndTime
     - ReasonsBefore, ReasonsAfter
     - Attempt, Outcome
   - Keep full subtitle text out of logs by default.

3. **Post-retry safety assertions**
   - Guard invariants after retry pass:
     - position unchanged
     - start/end timestamps unchanged
     - cue count unchanged

4. **Expanded test coverage**
   - Add tests for cancellation during retry.
   - Add tests for transient provider exception + fallback continuity.
   - Add tests validating unchanged cue metadata after retries.



### Test dataset notes (May 12, 2026)
- A baseline set of **8 subtitle pairs** (EN original + pt-BR translated by local LLM) was added for selective-retry validation.
- Use these pairs to compare:
  - suspicious-line detection precision
  - retry improvement rate vs conservative fallback
  - timestamp/index/ordering invariants before vs after retry
- Recommendation: run all Phase 1 expansion tests against this corpus before enabling broader UI rollout.

---

## Phase 2 — UI configuration (minimal) ✅ Implemented (May 12, 2026)

### Goals
- Expose current backend settings safely and clearly.
- Keep defaults conservative.

### UI placement
- `Lingarr.Client/src/components/features/settings/TranslationSettings.vue`
- `Lingarr.Client/src/ts/setting.ts`
- Settings store wiring where existing translation keys are updated.

### UI fields
1. **Enable selective retry** (`boolean`)
2. **Max retry attempts per cue** (`int`, min 0, max 2)
3. **Retry high severity only** (`boolean`, default true)
4. **Provider scope** (`llm_only` | `all`)
5. **Log retry attempts** (`boolean`)

### UX notes
- Show a warning badge when `provider_scope = all` due to potential cost/time increase.
- Keep advanced settings collapsed by default.

---

## Phase 3 — UI reporting (request detail) ✅ Implemented (May 12, 2026)

### Goals
- Let users see if selective retry was effective.
- Provide operational visibility without exposing private text.

### UI placement
- `Lingarr.Client/src/pages/TranslationDetailPage.vue`

### Reporting widgets
1. **Retry summary block**
   - attempted / improved / failed / skipped
2. **Reason distribution block**
   - counts by reason type (`prompt_leakage`, `cjk`, etc.)
3. **Outcome explanation**
   - short text clarifying conservative fallback when no improvement.

### Backend/API support
- Extend detail endpoint payload with retry summary metrics.
- Keep subtitle content excluded from retry metrics payload.

---

## Phase 4 — Quality improvements (analyzer + decisioning) ✅ Implemented (May 12, 2026)

### Goals
- Reduce false positives.
- Improve retry targeting precision.

### Improvements
1. **Severity model upgrade**
   - Replace static high/low split with weighted score.
   - Configurable threshold for retry eligibility.

2. **Language-aware heuristics**
   - Reduce false `possible_english_leftover` positives for mixed-language content.
   - Improve CJK detection handling for bilingual subtitles.

3. **Cue-structure preservation scoring**
   - Penalize retry candidates that alter expected cue formatting too aggressively.

4. **Confidence-based fallback**
   - Compare original cleaned vs retry candidate with a quality score and accept only if score improves beyond margin.

---

## Phase 5 — Glossary / proper noun protection

### Goals
- Avoid unwanted named-entity drift.

### Improvements
1. **Optional glossary support**
   - Manual glossary map per language pair (source token -> preferred target token).

2. **Proper noun lock mode**
   - For selected entity patterns, preserve source token unless explicit mapping exists.

3. **Retry prompt enrichment**
   - Include immutable terms section in retry prompt for protected nouns.

---

## Phase 6 — Provider-specific optimization

### Goals
- Better quality/cost tradeoff per provider.

### Improvements
1. **Provider capability matrix**
   - Mark providers by reliability for corrective retry prompting.
2. **Adaptive retry strategy**
   - Different max attempts and prompt style per provider type.
3. **Backoff and timeout tuning**
   - Reuse existing timeout/retry configuration patterns safely.

---

## Rollout strategy
1. Keep default behavior conservative (`enabled=true`, `max_attempts=1`, `high_severity_only=true`, `scope=llm_only`).
2. Release UI config first (Phase 2), then reporting (Phase 3).
3. Gather telemetry/log metrics to tune analyzer and retry thresholds before Phase 4.
4. Add glossary/proper noun protection after baseline stabilizes.

---

## Definition of done (full feature)
- UI controls available and persisted.
- Translation detail shows retry summary metrics.
- Analyzer false positives reduced with language-aware scoring.
- Optional glossary/proper-noun protection integrated.
- No regressions in subtitle timing/index/order integrity.
