---
description: 设定并持续跟踪当前会话目标（/goal <目标>）
argument-hint: <目标描述>
---

# Goal

The user invoked `/goal`. The objective for the rest of this session:

> $ARGUMENTS

If the objective above is empty, ask one short question: "这次会话的目标是什么？" Then wait for the answer and apply the same structure to it. Do not invent a goal.

## What to do now

1. **Restate the objective** in one sentence, phrased as an outcome, not as an activity.
2. **Define success criteria**: 2–5 verifiable statements. Each one must be checkable by running something, reading a diff, or pointing at a file. Reject criteria such as "works well" or "code is clean" — replace them with an observable signal.
3. **List assumptions and open questions.** If an ambiguity would change the implementation, ask up to three concise questions before doing any work. If the objective is clear enough to start, state the assumption and proceed.
4. **Produce a checklist** of the concrete steps that reach the criteria, ordered by dependency. Mark the first step as the one you will do next.
5. **State the verification plan**: the exact commands, tests, or checks that will prove each criterion.

## How to behave for the rest of the session

- Keep the goal as the filter for scope. Decline work that does not serve it and say why in one line.
- After each meaningful step, report progress as `done / total` against the checklist, plus any criterion that is now satisfied.
- When you hit a blocker, state what is missing, what you already tried, and the two most reasonable options. Do not silently shrink the goal.
- If the user changes the objective, restate the goal block and rebuild the checklist; do not merge two goals silently.
- Only declare the goal complete when every criterion has observable evidence. Say which evidence proves each one.

## Output format

Respond with exactly these sections, in this order, and nothing else:

```text
Goal: <one sentence>
Success criteria:
- <criterion 1>
- <criterion 2>
Assumptions: <one line, or "none">
Plan:
1. <step> — verify: <how>
2. <step> — verify: <how>
Next: <the single next action>
```

Leave out any section that would be empty. Write in the language the user is using.
