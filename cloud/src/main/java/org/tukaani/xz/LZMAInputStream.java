package org.tukaani.xz;

import java.io.ByteArrayInputStream;
import java.io.IOException;
import java.io.InputStream;

// A small raw-LZMA decoder standing in for XZ for Java. JavaSteam uses only this constructor to unpack
// older Steam content chunks ("VZip"): the whole chunk is decoded up front (its size is known), then
// served as a stream. Follows the LZMA SDK's reference decoder (LzmaSpec).
public class LZMAInputStream extends InputStream {
    private final ByteArrayInputStream out;

    public LZMAInputStream(InputStream in, long uncompSize, byte propsByte, int dictSize, byte[] presetDict) throws IOException {
        int props = propsByte & 0xFF;
        if (props >= 9 * 5 * 5) throw new IOException("lzma: bad properties");
        int lc = props % 9;
        props /= 9;
        int lp = props % 5, pb = props / 5;
        if (uncompSize < 0 || uncompSize > Integer.MAX_VALUE) throw new IOException("lzma: size unknown");
        byte[] buf = new byte[(int) uncompSize];
        new Decoder(in, buf, lc, lp, pb).run();
        out = new ByteArrayInputStream(buf);
    }

    @Override
    public int read() {
        return out.read();
    }

    @Override
    public int read(byte[] b, int off, int len) {
        return out.read(b, off, len);
    }

    private static final class Decoder {
        private final InputStream in;
        private final byte[] out;
        private final int lc, lp, pb;
        private int range = 0xFFFFFFFF, code, pos;

        private final short[] lit;
        private final short[] isMatch = probs(192), isRep = probs(12), isRepG0 = probs(12), isRepG1 = probs(12), isRepG2 = probs(12),
                isRep0Long = probs(192), posDecoders = probs(115), align = probs(16);
        private final short[][] posSlot = new short[4][];
        private final Len lenDec = new Len(), repLenDec = new Len();

        Decoder(InputStream in, byte[] out, int lc, int lp, int pb) throws IOException {
            this.in = in;
            this.out = out;
            this.lc = lc;
            this.lp = lp;
            this.pb = pb;
            lit = probs(0x300 << (lc + lp));
            for (int i = 0; i < 4; i++) posSlot[i] = probs(64);
            if (in.read() != 0) throw new IOException("lzma: bad stream start");
            for (int i = 0; i < 4; i++) code = (code << 8) | next();
        }

        private static short[] probs(int n) {
            short[] p = new short[n];
            java.util.Arrays.fill(p, (short) 1024);
            return p;
        }

        private int next() throws IOException {
            int b = in.read();
            if (b < 0) throw new IOException("lzma: unexpected end");
            return b;
        }

        private int bit(short[] p, int i) throws IOException {
            int prob = p[i];
            int bound = (range >>> 11) * prob;
            int r;
            if (Integer.compareUnsigned(code, bound) < 0) {
                range = bound;
                p[i] = (short) (prob + ((2048 - prob) >>> 5));
                r = 0;
            } else {
                range -= bound;
                code -= bound;
                p[i] = (short) (prob - (prob >>> 5));
                r = 1;
            }
            if (Integer.compareUnsigned(range, 1 << 24) < 0) {
                range <<= 8;
                code = (code << 8) | next();
            }
            return r;
        }

        private int direct(int n) throws IOException {
            int res = 0;
            for (; n > 0; n--) {
                range >>>= 1;
                code -= range;
                int t = 0 - (code >>> 31);
                code += range & t;
                res = (res << 1) + (t + 1);
                if (Integer.compareUnsigned(range, 1 << 24) < 0) {
                    range <<= 8;
                    code = (code << 8) | next();
                }
            }
            return res;
        }

        private int tree(short[] p, int base, int bits) throws IOException {
            int m = 1;
            for (int i = 0; i < bits; i++) m = (m << 1) + bit(p, base + m);
            return m - (1 << bits);
        }

        private int reverse(short[] p, int base, int bits) throws IOException {
            int m = 1, sym = 0;
            for (int i = 0; i < bits; i++) {
                int b = bit(p, base + m);
                m = (m << 1) + b;
                sym |= b << i;
            }
            return sym;
        }

        private final class Len {
            final short[] choice = probs(2), low = probs(16 << 3), mid = probs(16 << 3), high = probs(256);

            int decode(int posState) throws IOException {
                if (bit(choice, 0) == 0) return tree(low, posState << 3, 3);
                if (bit(choice, 1) == 0) return 8 + tree(mid, posState << 3, 3);
                return 16 + tree(high, 0, 8);
            }
        }

        void run() throws IOException {
            int state = 0, rep0 = 0, rep1 = 0, rep2 = 0, rep3 = 0;
            int pbMask = (1 << pb) - 1;
            while (pos < out.length) {
                int posState = pos & pbMask;
                if (bit(isMatch, (state << 4) + posState) == 0) {
                    literal(state, rep0);
                    state = state < 4 ? 0 : state < 10 ? state - 3 : state - 6;
                    continue;
                }
                int len;
                if (bit(isRep, state) != 0) {
                    if (pos == 0) throw new IOException("lzma: rep at start");
                    if (bit(isRepG0, state) == 0) {
                        if (bit(isRep0Long, (state << 4) + posState) == 0) {
                            state = state < 7 ? 9 : 11;
                            out[pos] = out[pos - rep0 - 1];
                            pos++;
                            continue;
                        }
                    } else {
                        int dist;
                        if (bit(isRepG1, state) == 0) dist = rep1;
                        else {
                            if (bit(isRepG2, state) == 0) dist = rep2;
                            else {
                                dist = rep3;
                                rep3 = rep2;
                            }
                            rep2 = rep1;
                        }
                        rep1 = rep0;
                        rep0 = dist;
                    }
                    len = repLenDec.decode(posState);
                    state = state < 7 ? 8 : 11;
                } else {
                    rep3 = rep2;
                    rep2 = rep1;
                    rep1 = rep0;
                    len = lenDec.decode(posState);
                    state = state < 7 ? 7 : 10;
                    rep0 = distance(len);
                    if (rep0 == -1) break; // end marker
                    if (Integer.compareUnsigned(rep0, pos) >= 0) throw new IOException("lzma: distance");
                }
                len += 2;
                if (len > out.length - pos) throw new IOException("lzma: overrun");
                int from = pos - rep0 - 1;
                for (int i = 0; i < len; i++) out[pos++] = out[from++];
            }
        }

        private void literal(int state, int rep0) throws IOException {
            int prev = pos > 0 ? out[pos - 1] & 0xFF : 0;
            int ls = ((pos & ((1 << lp) - 1)) << lc) + (prev >>> (8 - lc));
            int base = 0x300 * ls;
            int sym = 1;
            if (state >= 7) {
                int match = out[pos - rep0 - 1] & 0xFF;
                do {
                    int mb = (match >>> 7) & 1;
                    match <<= 1;
                    int b = bit(lit, base + ((1 + mb) << 8) + sym);
                    sym = (sym << 1) | b;
                    if (mb != b) break;
                } while (sym < 0x100);
            }
            while (sym < 0x100) sym = (sym << 1) | bit(lit, base + sym);
            out[pos++] = (byte) (sym - 0x100);
        }

        private int distance(int len) throws IOException {
            int ls = Math.min(len, 3);
            int slot = tree(posSlot[ls], 0, 6);
            if (slot < 4) return slot;
            int nd = (slot >>> 1) - 1;
            int dist = (2 | (slot & 1)) << nd;
            if (slot < 14) dist += reverse(posDecoders, dist - slot, nd);
            else {
                dist += direct(nd - 4) << 4;
                dist += reverse(align, 0, 4);
            }
            return dist;
        }
    }
}
