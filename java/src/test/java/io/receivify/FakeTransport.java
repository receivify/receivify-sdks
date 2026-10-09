package io.receivify;

import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.time.Duration;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.Deque;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;

/** An offline {@link Transport}: records every request and answers from a queue. */
final class FakeTransport implements Transport {
    interface Step {
        Response answer(Request request) throws IOException;
    }

    final List<Request> requests = new ArrayList<>();
    private final Deque<Step> steps = new ArrayDeque<>();

    FakeTransport reply(int status, String body) {
        return reply(status, body, new LinkedHashMap<String, String>());
    }

    FakeTransport reply(int status, String body, Map<String, String> headers) {
        steps.add(r -> new Response(status, headers, body.getBytes(StandardCharsets.UTF_8)));
        return this;
    }

    FakeTransport fail(IOException e) {
        steps.add(r -> {
            throw e;
        });
        return this;
    }

    @Override
    public Response send(Request request) throws IOException {
        requests.add(request);
        Step step = steps.poll();
        if (step == null) {
            throw new AssertionError("Unexpected request: " + request);
        }
        return step.answer(request);
    }

    Request last() {
        return requests.get(requests.size() - 1);
    }

    String lastBody() {
        byte[] b = last().getBody();
        return b == null ? null : new String(b, StandardCharsets.UTF_8);
    }

    /** A client wired to this transport with instant retries and recorded sleeps. */
    ReceivifyClient client(List<Duration> sleeps) {
        return ReceivifyClient.builder()
                .apiUrl("https://api.test/")
                .apiKey("rcv_live_secretkey123")
                .transport(this)
                .sleeper(d -> sleeps.add(d))
                .build();
    }

    ReceivifyClient client() {
        return client(new ArrayList<Duration>());
    }

    static Map<String, String> headers(String... kv) {
        Map<String, String> m = new LinkedHashMap<>();
        for (int i = 0; i < kv.length; i += 2) {
            m.put(kv[i], kv[i + 1]);
        }
        return m;
    }
}
