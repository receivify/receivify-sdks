package io.receivify;

/** Entry point for the offline suite: {@code java -cp target/classes:target/test-classes io.receivify.RunTests}. */
public final class RunTests {
    private RunTests() {
    }

    public static void main(String[] args) {
        System.out.println("receivify-java " + ReceivifyClient.VERSION + " on Java " + System.getProperty("java.version"));
        JsonTests.run();
        MoneyTests.run();
        WebhookTests.run();
        ClientTests.run();
        System.exit(T.summarise());
    }
}
