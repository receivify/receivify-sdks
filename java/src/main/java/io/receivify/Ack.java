package io.receivify;

import java.util.Map;

/**
 * A small acknowledgement body such as {@code {"ok":true,"cancelled":"<id>"}},
 * {@code {"ok":true,"revoked":"<id>"}}, {@code {"ok":true,"deleted":"<id>"}} or the settings
 * update ack. Read the echoed fields with {@link #get(String)} or {@link #getString(String)}.
 */
public final class Ack extends ReceivifyObject {
    public Ack(Map<String, Object> raw) {
        super(raw);
    }

    /** @return the {@code ok} flag */
    public boolean isOk() {
        return bool("ok");
    }

    /**
     * @param key a property such as {@code cancelled}, {@code revoked}, {@code deleted},
     *     {@code replayed}
     * @return its string value, or {@code null}
     */
    public String getString(String key) {
        return string(key);
    }
}
