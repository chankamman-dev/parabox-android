package com.github.luben.zstd;

// A small pure-Java Zstandard decoder (RFC 8878), standing in for zstd-jni. JavaSteam calls only
// Zstd.decompress(dst, src) to unpack Steam content chunks ("VSZa"), so that is all this provides:
// frames with raw, RLE and compressed blocks, Huffman literals and FSE sequences. No dictionaries.
// Correctness is checked by Steam itself: every chunk carries a checksum and every file a SHA-1.
public final class Zstd {
    private Zstd() { }

    public static long decompress(byte[] dst, byte[] src) {
        return new Decoder(src, dst).run();
    }

    public static boolean isError(long code) {
        return code < 0;
    }

    static final class Corrupt extends RuntimeException {
        Corrupt(String m) { super("zstd: " + m); }
    }

    private static final class Decoder {
        private static final int[] LL_BASE = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 18, 20, 22, 24, 28, 32, 40, 48, 64,
                128, 256, 512, 1024, 2048, 4096, 8192, 16384, 32768, 65536 };
        private static final int[] LL_BITS = { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 3, 3, 4, 6, 7, 8, 9, 10, 11,
                12, 13, 14, 15, 16 };
        private static final int[] ML_BASE = new int[53], ML_BITS = new int[53];
        private static final short[] LL_DEFAULT = { 4, 3, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2, 2, 2, 3, 2, 1, 1, 1,
                1, 1, -1, -1, -1, -1 };
        private static final short[] ML_DEFAULT = { 1, 4, 3, 2, 2, 2, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
                1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, -1, -1, -1, -1, -1, -1, -1 };
        private static final short[] OF_DEFAULT = { 1, 1, 1, 1, 1, 1, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, -1, -1, -1, -1,
                -1 };

        static {
            for (int i = 0; i < 32; i++) ML_BASE[i] = i + 3;
            int[] b = { 35, 37, 39, 41, 43, 47, 51, 59, 67, 83, 99, 131, 259, 515, 1027, 2051, 4099, 8195, 16387, 32771, 65539 };
            int[] n = { 1, 1, 1, 1, 2, 2, 3, 3, 4, 4, 5, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };
            for (int i = 0; i < b.length; i++) {
                ML_BASE[32 + i] = b[i];
                ML_BITS[32 + i] = n[i];
            }
        }

        private final byte[] src, dst;
        private int ip, op;
        private final int[] rep = new int[3];
        private Fse llTable, ofTable, mlTable;
        private Huff huff;

        Decoder(byte[] src, byte[] dst) {
            this.src = src;
            this.dst = dst;
        }

        long run() {
            while (ip + 4 <= src.length) {
                int magic = le32(ip);
                if ((magic & 0xFFFFFFF0) == 0x184D2A50) { // skippable frame
                    ip += 8 + le32(ip + 4);
                    continue;
                }
                if (magic != 0xFD2FB528) throw new Corrupt("bad magic");
                ip += 4;
                frame();
            }
            return op;
        }

        private void frame() {
            int fhd = u8(ip++);
            int fcsFlag = fhd >>> 6, single = (fhd >>> 5) & 1, checksum = (fhd >>> 2) & 1, dictFlag = fhd & 3;
            if ((fhd & 8) != 0) throw new Corrupt("reserved bit");
            if (single == 0) ip++; // window descriptor
            ip += dictFlag == 3 ? 4 : dictFlag;
            int fcsSize = fcsFlag == 0 ? single : fcsFlag == 1 ? 2 : fcsFlag == 2 ? 4 : 8;
            ip += fcsSize;
            rep[0] = 1; rep[1] = 4; rep[2] = 8;
            llTable = ofTable = mlTable = null;
            huff = null;
            boolean last;
            do {
                int bh = u8(ip) | u8(ip + 1) << 8 | u8(ip + 2) << 16;
                ip += 3;
                last = (bh & 1) != 0;
                int type = (bh >>> 1) & 3, size = bh >>> 3;
                if (type == 0) {
                    System.arraycopy(src, ip, dst, op, size);
                    ip += size;
                    op += size;
                } else if (type == 1) {
                    byte v = src[ip++];
                    for (int i = 0; i < size; i++) dst[op++] = v;
                } else if (type == 2) {
                    block(ip, ip + size);
                    ip += size;
                } else throw new Corrupt("reserved block type");
            } while (!last);
            if (checksum != 0) ip += 4;
        }

        // ---- compressed block ----

        private void block(int start, int end) {
            int p = start;
            int b0 = u8(p);
            int ltype = b0 & 3, sf = (b0 >>> 2) & 3;
            byte[] lit;
            int litLen;
            if (ltype == 0 || ltype == 1) {
                int hsize;
                if ((sf & 1) == 0) { litLen = b0 >>> 3; hsize = 1; }
                else if (sf == 1) { litLen = (b0 >>> 4) + (u8(p + 1) << 4); hsize = 2; }
                else { litLen = (b0 >>> 4) + (u8(p + 1) << 4) + (u8(p + 2) << 12); hsize = 3; }
                p += hsize;
                lit = new byte[litLen];
                if (ltype == 0) {
                    System.arraycopy(src, p, lit, 0, litLen);
                    p += litLen;
                } else {
                    java.util.Arrays.fill(lit, src[p]);
                    p += 1;
                }
            } else {
                int hsize, bits;
                boolean four;
                if (sf == 0) { four = false; hsize = 3; bits = 10; }
                else if (sf == 1) { four = true; hsize = 3; bits = 10; }
                else if (sf == 2) { four = true; hsize = 4; bits = 14; }
                else { four = true; hsize = 5; bits = 18; }
                long h = 0;
                for (int i = 0; i < hsize; i++) h |= (long) u8(p + i) << (8 * i);
                int mask = (1 << bits) - 1;
                litLen = (int) (h >>> 4) & mask;
                int compSize = (int) (h >>> (4 + bits)) & mask;
                p += hsize;
                int litEnd = p + compSize;
                if (ltype == 2) {
                    huff = new Huff();
                    p = huff.read(p);
                } else if (huff == null) throw new Corrupt("treeless literals without table");
                lit = new byte[litLen];
                if (!four) huff.stream(p, litEnd, lit, 0, litLen);
                else {
                    int s1 = u16(p), s2 = u16(p + 2), s3 = u16(p + 4);
                    int q = p + 6, seg = (litLen + 3) / 4;
                    huff.stream(q, q + s1, lit, 0, seg);
                    q += s1;
                    huff.stream(q, q + s2, lit, seg, seg);
                    q += s2;
                    huff.stream(q, q + s3, lit, 2 * seg, seg);
                    q += s3;
                    huff.stream(q, litEnd, lit, 3 * seg, litLen - 3 * seg);
                }
                p = litEnd;
            }

            // sequences
            int nbSeq = u8(p++);
            if (nbSeq >= 128) {
                if (nbSeq < 255) nbSeq = ((nbSeq - 128) << 8) + u8(p++);
                else { nbSeq = u8(p) + (u8(p + 1) << 8) + 0x7F00; p += 2; }
            }
            if (nbSeq == 0) {
                System.arraycopy(lit, 0, dst, op, litLen);
                op += litLen;
                return;
            }
            int modes = u8(p++);
            int[] pp = { p };
            llTable = table(modes >>> 6, LL_DEFAULT, 6, 35, 9, llTable, pp);
            ofTable = table((modes >>> 4) & 3, OF_DEFAULT, 5, 31, 8, ofTable, pp);
            mlTable = table((modes >>> 2) & 3, ML_DEFAULT, 6, 52, 9, mlTable, pp);
            p = pp[0];

            BitIn in = new BitIn(src, p, end);
            int sLL = (int) in.read(llTable.log), sOF = (int) in.read(ofTable.log), sML = (int) in.read(mlTable.log);
            int litPos = 0;
            for (int i = 0; i < nbSeq; i++) {
                int ofCode = ofTable.sym[sOF], llCode = llTable.sym[sLL], mlCode = mlTable.sym[sML];
                if (ofCode > 31 || llCode > 35 || mlCode > 52) throw new Corrupt("bad code");
                long ofValue = (1L << ofCode) + in.read(ofCode);
                int ml = ML_BASE[mlCode] + (int) in.read(ML_BITS[mlCode]);
                int ll = LL_BASE[llCode] + (int) in.read(LL_BITS[llCode]);
                if (i != nbSeq - 1) {
                    sLL = llTable.base[sLL] + (int) in.read(llTable.bits[sLL]);
                    sML = mlTable.base[sML] + (int) in.read(mlTable.bits[sML]);
                    sOF = ofTable.base[sOF] + (int) in.read(ofTable.bits[sOF]);
                }
                int offset;
                if (ofValue > 3) {
                    offset = (int) (ofValue - 3);
                    rep[2] = rep[1];
                    rep[1] = rep[0];
                    rep[0] = offset;
                } else {
                    int idx = (int) ofValue - 1 + (ll == 0 ? 1 : 0);
                    if (idx == 0) offset = rep[0];
                    else {
                        offset = idx == 3 ? rep[0] - 1 : rep[idx];
                        if (offset == 0) offset = 1;
                        if (idx != 1) rep[2] = rep[1];
                        rep[1] = rep[0];
                        rep[0] = offset;
                    }
                }
                if (litPos + ll > litLen) throw new Corrupt("literals overrun");
                System.arraycopy(lit, litPos, dst, op, ll);
                litPos += ll;
                op += ll;
                int from = op - offset;
                if (from < 0) throw new Corrupt("offset before start");
                if (offset >= ml) {
                    System.arraycopy(dst, from, dst, op, ml);
                    op += ml;
                } else {
                    for (int k = 0; k < ml; k++) dst[op++] = dst[from++];
                }
            }
            int rest = litLen - litPos;
            System.arraycopy(lit, litPos, dst, op, rest);
            op += rest;
        }

        private Fse table(int mode, short[] def, int defLog, int maxSym, int maxLog, Fse prev, int[] pp) {
            switch (mode) {
                case 0:
                    return Fse.build(def, def.length - 1, defLog);
                case 1: {
                    Fse t = new Fse(0);
                    t.sym[0] = u8(pp[0]++);
                    return t;
                }
                case 2: {
                    short[] norm = new short[maxSym + 1];
                    int[] res = readNCount(pp[0], norm, maxSym);
                    if (res[1] > maxLog) throw new Corrupt("table log too large");
                    pp[0] += res[0];
                    return Fse.build(norm, res[2], res[1]);
                }
                default:
                    if (prev == null) throw new Corrupt("repeat without table");
                    return prev;
            }
        }

        // FSE normalized counts; returns {bytes used, table log, max symbol}.
        private int[] readNCount(int start, short[] norm, int maxSym) {
            int p = start;
            int bitStream = le32safe(p);
            int nbBits = (bitStream & 0xF) + 5;
            int tableLog = nbBits;
            bitStream >>>= 4;
            int bitCount = 4;
            int remaining = (1 << nbBits) + 1, threshold = 1 << nbBits;
            nbBits++;
            int sym = 0;
            boolean prev0 = false;
            while (remaining > 1 && sym <= maxSym) {
                if (prev0) {
                    int n0 = sym;
                    while ((bitStream & 0xFFFF) == 0xFFFF) {
                        n0 += 24;
                        p += 2;
                        bitStream = le32safe(p) >>> bitCount;
                    }
                    while ((bitStream & 3) == 3) {
                        n0 += 3;
                        bitStream >>>= 2;
                        bitCount += 2;
                    }
                    n0 += bitStream & 3;
                    bitCount += 2;
                    if (n0 > maxSym + 1) throw new Corrupt("ncount overflow");
                    while (sym < n0) norm[sym++] = 0;
                    p += bitCount >>> 3;
                    bitCount &= 7;
                    bitStream = le32safe(p) >>> bitCount;
                }
                int max = (2 * threshold - 1) - remaining;
                int count;
                if ((bitStream & (threshold - 1)) < max) {
                    count = bitStream & (threshold - 1);
                    bitCount += nbBits - 1;
                } else {
                    count = bitStream & (2 * threshold - 1);
                    if (count >= threshold) count -= max;
                    bitCount += nbBits;
                }
                count--;
                remaining -= count < 0 ? -count : count;
                if (sym > maxSym) throw new Corrupt("too many symbols");
                norm[sym++] = (short) count;
                prev0 = count == 0;
                while (remaining < threshold) {
                    nbBits--;
                    threshold >>= 1;
                }
                p += bitCount >>> 3;
                bitCount &= 7;
                bitStream = le32safe(p) >>> bitCount;
            }
            if (remaining != 1) throw new Corrupt("bad ncount");
            return new int[] { p + ((bitCount + 7) >>> 3) - start, tableLog, sym - 1 };
        }

        // ---- FSE decoding table ----
        static final class Fse {
            final int log;
            final int[] sym, bits, base;

            Fse(int log) {
                this.log = log;
                int n = 1 << log;
                sym = new int[n];
                bits = new int[n];
                base = new int[n];
            }

            static Fse build(short[] norm, int maxSym, int log) {
                Fse t = new Fse(log);
                int size = 1 << log, high = size - 1;
                int[] next = new int[maxSym + 1];
                for (int s = 0; s <= maxSym; s++) {
                    if (norm[s] == -1) {
                        t.sym[high--] = s;
                        next[s] = 1;
                    } else next[s] = norm[s];
                }
                int step = (size >>> 1) + (size >>> 3) + 3, mask = size - 1, pos = 0;
                for (int s = 0; s <= maxSym; s++) {
                    for (int i = 0; i < norm[s]; i++) {
                        t.sym[pos] = s;
                        do pos = (pos + step) & mask; while (pos > high);
                    }
                }
                if (pos != 0) throw new Corrupt("fse spread");
                for (int u = 0; u < size; u++) {
                    int s = t.sym[u];
                    int ns = next[s]++;
                    int nb = log - (31 - Integer.numberOfLeadingZeros(ns));
                    t.bits[u] = nb;
                    t.base[u] = (ns << nb) - size;
                }
                return t;
            }
        }

        // ---- Huffman literals ----
        final class Huff {
            int log;
            byte[] sym;
            byte[] len;

            int read(int p) {
                int hb = u8(p++);
                int[] w = new int[256];
                int n;
                if (hb >= 128) {
                    n = hb - 127;
                    for (int i = 0; i < n; i++) {
                        int b = u8(p + i / 2);
                        w[i] = (i & 1) == 0 ? b >>> 4 : b & 15;
                    }
                    p += (n + 1) / 2;
                } else {
                    int end = p + hb;
                    short[] norm = new short[256];
                    int[] res = readNCount(p, norm, 255);
                    if (res[1] > 6) throw new Corrupt("huffman weight log");
                    Fse t = Fse.build(norm, res[2], res[1]);
                    BitIn in = new BitIn(src, p + res[0], end);
                    int s1 = (int) in.read(t.log), s2 = (int) in.read(t.log);
                    n = 0;
                    while (true) {
                        if (n >= 255) throw new Corrupt("too many weights");
                        w[n++] = t.sym[s1];
                        s1 = t.base[s1] + (int) in.read(t.bits[s1]);
                        if (in.overflow()) { w[n++] = t.sym[s2]; break; }
                        if (n >= 255) throw new Corrupt("too many weights");
                        w[n++] = t.sym[s2];
                        s2 = t.base[s2] + (int) in.read(t.bits[s2]);
                        if (in.overflow()) { w[n++] = t.sym[s1]; break; }
                    }
                    p = end;
                }
                // The last weight is implied: the weights must sum (as 2^(w-1)) to a power of two.
                long total = 0;
                for (int i = 0; i < n; i++) if (w[i] > 0) total += 1L << (w[i] - 1);
                if (total == 0) throw new Corrupt("empty huffman");
                int maxBits = 64 - Long.numberOfLeadingZeros(total);
                long left = (1L << maxBits) - total;
                if (Long.bitCount(left) != 1) throw new Corrupt("huffman weights");
                w[n++] = 64 - Long.numberOfLeadingZeros(left);
                log = maxBits;
                if (log > 11) throw new Corrupt("huffman too deep");
                int size = 1 << log;
                sym = new byte[size];
                len = new byte[size];
                int[] rankCount = new int[log + 2];
                for (int i = 0; i < n; i++) rankCount[w[i]]++;
                int[] rankStart = new int[log + 2];
                int next = 0;
                for (int r = 1; r <= log; r++) {
                    rankStart[r] = next;
                    next += rankCount[r] << (r - 1);
                }
                for (int s = 0; s < n; s++) {
                    int wt = w[s];
                    if (wt == 0) continue;
                    int length = (1 << wt) >>> 1;
                    int st = rankStart[wt];
                    for (int u = st; u < st + length; u++) {
                        sym[u] = (byte) s;
                        len[u] = (byte) (log + 1 - wt);
                    }
                    rankStart[wt] += length;
                }
                return p;
            }

            void stream(int start, int end, byte[] out, int off, int count) {
                BitIn in = new BitIn(src, start, end);
                for (int i = 0; i < count; i++) {
                    int v = (int) in.peek(log);
                    out[off + i] = sym[v];
                    in.skip(len[v]);
                }
            }
        }

        // Backward bit stream: starts at the highest set bit of the last byte, reads toward the start.
        static final class BitIn {
            private final byte[] b;
            private final int start, end;
            private long pos; // bits still unread above the start

            BitIn(byte[] b, int start, int end) {
                if (end <= start) throw new Corrupt("empty bitstream");
                this.b = b;
                this.start = start;
                this.end = end;
                int last = b[end - 1] & 0xFF;
                if (last == 0) throw new Corrupt("bitstream end mark");
                pos = (long) (end - start - 1) * 8 + (31 - Integer.numberOfLeadingZeros(last));
            }

            long read(int n) {
                if (n == 0) return 0;
                long v = peek(n);
                pos -= n;
                return v;
            }

            void skip(int n) {
                pos -= n;
            }

            boolean overflow() {
                return pos < 0;
            }

            long peek(int n) {
                long p = pos - n;
                if (p < 0) {
                    // Bits below the start read as zero.
                    if (pos <= 0) return 0;
                    return bitsAt(0, (int) pos) << (int) -p;
                }
                return bitsAt(p, n);
            }

            private long bitsAt(long p, int n) {
                int byteIdx = start + (int) (p >>> 3);
                int shift = (int) (p & 7);
                long v = 0;
                for (int i = 0; i < 8; i++) {
                    int k = byteIdx + i;
                    if (k >= end) break;
                    v |= (long) (b[k] & 0xFF) << (8 * i);
                }
                return (v >>> shift) & ((1L << n) - 1);
            }
        }

        private int u8(int p) {
            return src[p] & 0xFF;
        }

        private int u16(int p) {
            return u8(p) | u8(p + 1) << 8;
        }

        private int le32(int p) {
            return u8(p) | u8(p + 1) << 8 | u8(p + 2) << 16 | u8(p + 3) << 24;
        }

        private int le32safe(int p) {
            int v = 0;
            for (int i = 0; i < 4; i++) if (p + i < src.length) v |= u8(p + i) << (8 * i);
            return v;
        }
    }
}
