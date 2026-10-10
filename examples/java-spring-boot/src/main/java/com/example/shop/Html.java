package com.example.shop;

/** Minimal page shell, so the example has no template engine to learn. */
final class Html {
    private Html() {}

    static String page(String title, String body) {
        return "<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">"
                + "<title>" + title + "</title><style>body{font:16px/1.5 system-ui,sans-serif;max-width:32rem;margin:4rem auto;padding:0 1rem}"
                + ".price{font-size:2rem;font-weight:700}label{display:block;margin:1rem 0}"
                + "button{background:#a3e635;border:0;padding:.6rem 1rem;border-radius:.6rem;font-weight:600}"
                + ".status{font-weight:700}.paid{color:#4d7c0f}.muted{color:#71717a;font-size:.9rem}</style></head><body>"
                + body + "</body></html>";
    }
}
