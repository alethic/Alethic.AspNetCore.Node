/**
 * A plain JavaScript module with no web anywhere in sight — what the pool runs when it is not
 * rendering. A self-contained CommonJS bundle in real use; hand-written here because it needs
 * nothing beyond the platform.
 */

/** Synchronous work: turn a title into a URL slug. */
module.exports.slugify = function (text) {
    return text
        .toLowerCase()
        .normalize('NFKD')
        .replace(/[^a-z0-9\s-]/g, '')
        .trim()
        .replace(/\s+/g, '-');
};

/** Asynchronous work through a real platform API: SHA-256 via WebCrypto. */
module.exports.digest = async function (text) {
    const bytes = new TextEncoder().encode(text);
    const hash = await crypto.subtle.digest('SHA-256', bytes);
    return Array.from(new Uint8Array(hash))
        .map(b => b.toString(16).padStart(2, '0'))
        .join('');
};

/** Structured data back to .NET: word and character counts. */
module.exports.stats = function (text) {
    return {
        characters: text.length,
        words: text.split(/\s+/).filter(w => w.length > 0).length,
    };
};
