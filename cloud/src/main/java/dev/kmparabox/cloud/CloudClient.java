package dev.kmparabox.cloud;

import java.io.ByteArrayInputStream;
import java.io.ByteArrayOutputStream;
import java.security.MessageDigest;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.TimeUnit;
import java.util.zip.Deflater;
import java.util.zip.ZipEntry;
import java.util.zip.ZipInputStream;
import java.util.zip.ZipOutputStream;

import com.google.protobuf.ByteString;

import in.dragonbra.javasteam.protobufs.steamclient.SteammessagesCloudSteamclient.CCloud_ClientBeginFileUpload_Request;
import in.dragonbra.javasteam.protobufs.steamclient.SteammessagesCloudSteamclient.CCloud_ClientBeginFileUpload_Response;
import in.dragonbra.javasteam.protobufs.steamclient.SteammessagesCloudSteamclient.CCloud_ClientCommitFileUpload_Request;
import in.dragonbra.javasteam.protobufs.steamclient.SteammessagesCloudSteamclient.CCloud_ClientDeleteFile_Request;
import in.dragonbra.javasteam.protobufs.steamclient.SteammessagesCloudSteamclient.CCloud_ClientFileDownload_Request;
import in.dragonbra.javasteam.protobufs.steamclient.SteammessagesCloudSteamclient.CCloud_ClientFileDownload_Response;
import in.dragonbra.javasteam.protobufs.steamclient.SteammessagesCloudSteamclient.CCloud_EnumerateUserFiles_Request;
import in.dragonbra.javasteam.protobufs.steamclient.SteammessagesCloudSteamclient.CCloud_EnumerateUserFiles_Response;
import in.dragonbra.javasteam.protobufs.steamclient.SteammessagesCloudSteamclient.CCloud_UserFile;
import in.dragonbra.javasteam.rpc.service.Cloud;
import in.dragonbra.javasteam.steam.handlers.steamunifiedmessages.SteamUnifiedMessages;
import okhttp3.MediaType;
import okhttp3.OkHttpClient;
import okhttp3.Request;
import okhttp3.RequestBody;
import okhttp3.Response;

// Steam Cloud's per-file protocol: enumerate, download through the CDN URL Steam hands out, and
// begin/HTTP blocks/commit for uploads. Protocol details follow SilksongAndroid (MIT).
final class CloudClient {
    static final class CloudFile {
        final String name;
        final long timestamp;
        final String sha;

        CloudFile(String name, long timestamp, String sha) {
            this.name = name;
            this.timestamp = timestamp;
            this.sha = sha;
        }
    }

    private final Cloud cloud;
    private final OkHttpClient http = new OkHttpClient.Builder()
            .connectTimeout(30, TimeUnit.SECONDS).readTimeout(60, TimeUnit.SECONDS).callTimeout(120, TimeUnit.SECONDS).build();

    CloudClient(SteamSession s) {
        cloud = s.client.getHandler(SteamUnifiedMessages.class).createService(Cloud.class);
    }

    List<CloudFile> list(int appId) throws Exception {
        List<CloudFile> all = new ArrayList<>();
        int start = 0;
        while (true) {
            CCloud_EnumerateUserFiles_Request req = CCloud_EnumerateUserFiles_Request.newBuilder()
                    .setAppid(appId).setStartIndex(start).setCount(500).build();
            var body = cloud.enumerateUserFiles(req).toFuture().get(30, TimeUnit.SECONDS).getBody();
            List<CCloud_UserFile> files = body.getFilesList();
            for (CCloud_UserFile f : files) all.add(new CloudFile(f.getFilename(), f.getTimestamp(), f.getFileSha()));
            start += files.size();
            if (files.size() < 500) break;
        }
        return all;
    }

    byte[] download(int appId, String name) throws Exception {
        CCloud_ClientFileDownload_Request req = CCloud_ClientFileDownload_Request.newBuilder().setAppid(appId).setFilename(name).build();
        var b = cloud.clientFileDownload(req).toFuture().get(30, TimeUnit.SECONDS).getBody();
        if (b.getUrlHost().isEmpty()) throw new Exception("云端没有给出 " + name + " 的下载地址");
        Request.Builder rb = new Request.Builder().url((b.getUseHttps() ? "https://" : "http://") + b.getUrlHost() + b.getUrlPath());
        for (var h : b.getRequestHeadersList()) rb.addHeader(h.getName(), h.getValue());
        try (Response r = http.newCall(rb.build()).execute()) {
            if (!r.isSuccessful()) throw new Exception("下载 " + name + " 失败：HTTP " + r.code());
            byte[] raw = r.body().bytes();
            boolean zipped = b.getRawFileSize() > 0 && b.getRawFileSize() != b.getFileSize()
                    && raw.length >= 4 && raw[0] == 0x50 && raw[1] == 0x4B && raw[2] == 0x03 && raw[3] == 0x04;
            return zipped ? unzip(raw) : raw;
        }
    }

    void upload(int appId, String name, byte[] content, long timestamp) throws Exception {
        byte[] sha = MessageDigest.getInstance("SHA-1").digest(content);
        ByteString shaBytes = ByteString.copyFrom(sha);
        byte[] body = zip(content);
        if (body.length >= content.length) body = content;
        CCloud_ClientBeginFileUpload_Request begin = CCloud_ClientBeginFileUpload_Request.newBuilder()
                .setAppid(appId).setFilename(name).setFileSize(body.length).setRawFileSize(content.length)
                .setFileSha(shaBytes).setTimeStamp(timestamp).setCanEncrypt(false).setIsSharedFile(false).build();
        var br = cloud.clientBeginFileUpload(begin).toFuture().get(30, TimeUnit.SECONDS).getBody();
        boolean ok = false;
        try {
            for (var blk : br.getBlockRequestsList()) {
                byte[] part;
                if (blk.hasExplicitBodyData() && blk.getExplicitBodyData().size() > 0) part = blk.getExplicitBodyData().toByteArray();
                else {
                    part = new byte[blk.getBlockLength()];
                    System.arraycopy(body, (int) blk.getBlockOffset(), part, 0, part.length);
                }
                Request.Builder rb = new Request.Builder().url((blk.getUseHttps() ? "https://" : "http://") + blk.getUrlHost() + blk.getUrlPath());
                for (var h : blk.getRequestHeadersList()) rb.addHeader(h.getName(), h.getValue());
                RequestBody rqb = RequestBody.create(part, MediaType.get("application/octet-stream"));
                if (blk.getHttpMethod() == 2) rb.post(rqb); else rb.put(rqb);
                try (Response r = http.newCall(rb.build()).execute()) {
                    if (!r.isSuccessful()) throw new Exception("上传 " + name + " 失败：HTTP " + r.code());
                }
            }
            ok = true;
        } finally {
            CCloud_ClientCommitFileUpload_Request commit = CCloud_ClientCommitFileUpload_Request.newBuilder()
                    .setAppid(appId).setFilename(name).setFileSha(shaBytes).setTransferSucceeded(ok).build();
            try {
                cloud.clientCommitFileUpload(commit).toFuture().get(30, TimeUnit.SECONDS);
            } catch (Throwable ignored) {
            }
        }
    }

    void delete(int appId, String name) throws Exception {
        CCloud_ClientDeleteFile_Request req = CCloud_ClientDeleteFile_Request.newBuilder().setAppid(appId).setFilename(name).setIsExplicitDelete(true).build();
        cloud.clientDeleteFile(req).toFuture().get(30, TimeUnit.SECONDS);
    }

    // Steam wraps compressed files in a single-entry zip named "data".
    private static byte[] zip(byte[] raw) throws Exception {
        ByteArrayOutputStream o = new ByteArrayOutputStream(raw.length / 2 + 256);
        try (ZipOutputStream z = new ZipOutputStream(o)) {
            z.setLevel(Deflater.BEST_COMPRESSION);
            z.putNextEntry(new ZipEntry("data"));
            z.write(raw);
            z.closeEntry();
        }
        return o.toByteArray();
    }

    private static byte[] unzip(byte[] data) throws Exception {
        try (ZipInputStream z = new ZipInputStream(new ByteArrayInputStream(data))) {
            if (z.getNextEntry() == null) throw new Exception("云端压缩包是空的");
            ByteArrayOutputStream o = new ByteArrayOutputStream();
            byte[] buf = new byte[8192];
            for (int n; (n = z.read(buf)) > 0; ) o.write(buf, 0, n);
            return o.toByteArray();
        }
    }
}
