---
description: 为一次改动产出可执行的实施计划（/plan <任务描述>）
argument-hint: <任务描述>
---

# Plan

The user invoked `/plan`. Task to plan:

> $ARGUMENTS

If the task above is empty, ask "要为哪项改动制定计划？" and wait one turn. Do not fabricate a task.

## Ground the plan in the real codebase

Before writing the plan, collect evidence with the tools you have:

- Locate the files, types, and entry points the task touches. Prefer searching over guessing; never plan against files you have not confirmed exist.
- Read the current implementation of the code you intend to change, not just its name.
- Find every caller of the symbols you plan to change.
- Check how the project already solves similar problems, and plan to reuse that pattern rather than introducing a second one.

If a tool is unavailable, say which fact you could not confirm and mark the plan as provisional instead of inventing file paths.

## Write the plan

A plan is a sequence of steps that each leave the tree in a working state. For every step give:

- the files to create or change, by path;
- the change in one or two sentences: the API, symbol, or behavior;
- how it is verified: the exact command, test, or manual scenario;
- what breaks if the step is skipped or done wrong.

Order steps so shared foundations land first and callers migrate in the same step that removes the old path. Never plan a half-migrated state.

## Rules

- Keep the smallest design that satisfies the task. Name any abstraction you add and the concrete duplication it removes; drop abstractions you cannot justify.
- Call out risks explicitly: data loss, public API breaks, behavior changes, migrations. For each, give the safe option and the rollback.
- Do not plan tests that only re-assert wiring or copy implementation details; plan tests that would catch a plausible bug.
- Do not pad the plan with refactors the task did not ask for. If something is worth doing anyway, list it separately under "Out of scope".
- State what you did not verify. A plan that pretends to certainty is worse than one that names its gaps.

## Output format

Respond with exactly this structure, in this order. Write in the language the user is using.

```text
Context: <2–3 sentences: what exists today and why it does not satisfy the task>
Steps:
1. <title>
   Files: <paths>
   Change: <what and why>
   Verify: <exact check>
2. <title>
   ...
Risks: <bullets, each with the safe option>
Out of scope: <bullets, or "none">
Open questions: <bullets, or "none">
```

After the plan, stop. Do not start implementing until the user confirms.
