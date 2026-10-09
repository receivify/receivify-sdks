package io.receivify;

import java.time.Clock;
import java.time.Duration;
import java.util.Objects;

/** Knobs for webhook verification: the replay tolerance window and the clock. */
public final class WebhookVerifyOptions {
    /** The default replay window: 300 seconds. */
    public static final Duration DEFAULT_TOLERANCE = Duration.ofSeconds(300);

    private static final WebhookVerifyOptions DEFAULTS = new WebhookVerifyOptions(DEFAULT_TOLERANCE, Clock.systemUTC());

    private final Duration tolerance;
    private final Clock clock;

    private WebhookVerifyOptions(Duration tolerance, Clock clock) {
        this.tolerance = tolerance;
        this.clock = clock;
    }

    /** @return a 300-second tolerance against the system clock */
    public static WebhookVerifyOptions defaults() {
        return DEFAULTS;
    }

    /**
     * @param tolerance the replay window; {@link Duration#ZERO} disables the check (tests only)
     * @return options with that tolerance and the system clock
     */
    public static WebhookVerifyOptions tolerance(Duration tolerance) {
        return new WebhookVerifyOptions(Objects.requireNonNull(tolerance, "tolerance"), Clock.systemUTC());
    }

    /**
     * @param clock the clock "now" is read from (tests)
     * @return a copy using that clock
     */
    public WebhookVerifyOptions withClock(Clock clock) {
        return new WebhookVerifyOptions(tolerance, Objects.requireNonNull(clock, "clock"));
    }

    /**
     * @param tolerance the replay window; {@link Duration#ZERO} disables the check
     * @return a copy using that tolerance
     */
    public WebhookVerifyOptions withTolerance(Duration tolerance) {
        return new WebhookVerifyOptions(Objects.requireNonNull(tolerance, "tolerance"), clock);
    }

    /** @return the replay window */
    public Duration getTolerance() {
        return tolerance;
    }

    /** @return the clock */
    public Clock getClock() {
        return clock;
    }
}
