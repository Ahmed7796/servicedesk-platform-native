# Hand-off: M2-4b-ii overdue job scheduling

**Agent:** notify-spring  **Date:** 2026-11-19

This closes M2-4 in code: the overdue job now runs on a timer. Not committed. Not run in Compose (the lead's check).

## What changed
- `overdue/OverdueJobProperties`: `app.notify.overdue-job.interval` (env `NOTIFY_OVERDUE_JOB_INTERVAL`), bound as a String, parsed with `Duration.parse`, default `PT1M` (`@DefaultValue`). Zero, negative, empty, `60`, `1m`, `abc` fail startup with "NOTIFY_OVERDUE_JOB_INTERVAL must be a positive ISO-8601 duration such as PT1M, got: X". No chained cause (M2-3 lesson).
- `overdue/OverdueSchedulingConfig`: the only `@EnableScheduling`; `@ConditionalOnProperty(app.notify.overdue-job.enabled, matchIfMissing = true)`. `@Scheduled(fixedDelayString = "${app.notify.overdue-job.interval:PT1M}") tick()` calls `OverdueJob.runOnce()`. It also injects `OverdueJobProperties`, so the strict check runs at startup and the bad value never reaches the scheduler's own lenient parsing.
- `push/ExpoPushSender`: constructor now `(url, connectTimeout, readTimeout)`; production uses 10 s and 5 s.
- `application.yml`: `overdue-job.enabled: true`, `overdue-job.interval`. `application-test.yml`, `-dbtest.yml`, `-pipeline.yml`: `overdue-job.enabled: false`.
- `OverdueJob`: javadoc only. `runOnce` is still non-transactional; the reflection test still passes.
- No dependency, `pom.xml`, contract, Compose, `.env.example`, PLAN or build-log change. About 60 lines of main code, 110 of tests.

## How it was verified
- `./mvnw test` (Docker up): 169 tests, 0 failures, 0 errors (151 before, 18 new: 10 binding runs plus 8 scheduling-config runs). Unchanged after the review fix below.
- Mutations, both caught and restored: removing `matchIfMissing` failed `presentWhenTheFlagIsUnset` and the firing test; setting `enabled: true` in `application-pipeline.yml` failed the pipeline profile case.
- Acceptance (closing M2-4, "exactly one overdue push"): once-only is proven by M2-4b-i tests calling `runOnce()`; the timer is proven by the firing test. The Compose run is yours.

## Tests
- `OverdueJobPropertiesTest`: `PT1M`, `PT30S`, `PT0.5S` bind; unset gives 1 minute (via a context runner, as Spring creates the bean); `PT0S`, `-PT1M`, `60`, `1m`, `abc`, `PT1`, empty fail naming the variable and `PT1M`.
- `OverdueSchedulingConfigTest` (context runner, no Docker): present when the flag is unset; absent (config and `ScheduledAnnotationBeanPostProcessor`) when false; absent for `test`, `dbtest` and `pipeline` using the real yml files; with `PT0.1S` and a mock `OverdueJob`, `verify(timeout(3000).atLeast(2)).runOnce()` (returns after about 0.2 s, no sleeps); bad interval fails startup.
- `ExpoPushSenderTest`: only the constructor calls changed (`timeout, timeout`; `300ms, 300ms`). No assertion changed. I did not add a separate read-timeout test: the existing slow-server case (1.5 s delay, 200 ms timeout) already exercises the read timeout, since the connection to the local server succeeds at once.

## Review fixes
- The three strict-parsing tests (`OverdueJobPropertiesTest`, `OverdueSchedulingConfigTest`, `StepSlaPropertiesTest`) now assert `.rootCause().hasMessageContaining(...)` instead of `hasStackTraceContaining`, so chaining the `DateTimeParseException` later fails a test (checked: chaining it in `OverdueJobProperties` failed the binding test; restored). Only tightened.
- A tick that throws (realistically a `DataAccessException` from `claimDue`) is logged and suppressed by Spring's default handler for repeating tasks, so the schedule keeps running. Its message holds SQL and timestamp parameters only, no title or token.

## Decisions made
- `@Scheduled` uses the same property string; Spring reads ISO-8601 there natively. Alternative: SpEL on the properties bean, rejected as more fragile.
- The profile check loads the real ymls with `org.springframework.boot.test.context.ConfigDataApplicationContextInitializer` (Boot 4 package confirmed in `spring-boot-test`). Nothing awkward.

## Review this hardest
- The first tick fires at startup (no initial delay), then one interval after each run ends. Flyway has run by then (repositories depend on the datasource), and an empty table is a no-op. With a restart loop and a past-due step, a push goes out at once, but the `overdue_since` claim stops repeats.
- `fixedDelay` on Spring's default single-thread scheduler: no overlap within one instance. Two instances are covered by the M2-4b-i claim, not by this class.
- Because `@ConditionalOnProperty` defaults to on, any new profile that should not run the timer must set `enabled: false`.

## For the lead: Compose and .env.example (not edited by me)
- `docker-compose.yml`, notification-service environment, next to `NOTIFY_STEP_SLA` (line 123):
  `      NOTIFY_OVERDUE_JOB_INTERVAL: ${NOTIFY_OVERDUE_JOB_INTERVAL:-PT1M}`
- `.env.example`, next to `NOTIFY_STEP_SLA`:
  `# Delay between overdue-job runs, strict ISO-8601 (PT30S, PT1M). Not "60" or "1m". Default PT1M.`
  `# NOTIFY_OVERDUE_JOB_INTERVAL=PT1M`

## Not done / follow-ups
- Nothing outstanding for M2-4. Ahmed ticks PLAN.

## Contract or rule questions
- None.
