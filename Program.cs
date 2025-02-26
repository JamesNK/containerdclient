﻿using Containerd.Services.Containers.V1;
using Containerd.Services.Namespaces.V1;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Win32.SafeHandles;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using static Pipes;

const string sockPath = "/run/containerd/containerd.sock";
const string pipeName = @"\\.\pipe\containerd-containerd";
bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
Console.WriteLine($"Check pipe: {File.Exists(@"\\.\pipe\containerd-containerd")}");
using IConnectionFactory connectionFactory = isWindows ? new NamedPipeConnectionFactory(pipeName) : new UnixDomainSocketConnectionFactory(sockPath);
var socketsHttpHandler = new SocketsHttpHandler
{
    ConnectCallback = connectionFactory.ConnectAsync,
};

var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
{
    HttpHandler = socketsHttpHandler, 
    
});
var client = new Containers.ContainersClient(channel);
var headers = new Metadata
{
    { "containerd-namespace", "default" }
};


var listNamespaceRequest = new ListNamespacesRequest();
var namespaceClient = new Namespaces.NamespacesClient(channel);
var namespaces = await namespaceClient.ListAsync(listNamespaceRequest, headers);
foreach (var @namespace in namespaces.Namespaces)
{
    headers = new Metadata
    {
        { "containerd-namespace", @namespace.Name }
    };
    var response = await client.ListAsync(new ListContainersRequest(), headers);
    if (response == null)
    {
        Console.WriteLine("No response");
    }
    else
    {
        Console.WriteLine(response);
    }
}


public sealed class NamedPipeConnectionFactory : IConnectionFactory, IDisposable
{
    private SafePipeHandle handle;
    private NamedPipeClientStream _pipe;
    public NamedPipeConnectionFactory(string pipeName)
    {
        uint pipeFlags = FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_ANONYMOUS;
        uint fileAccess = GENERIC_READ | GENERIC_WRITE;
        int error;
        this.handle = CreateFileW(
            pipeName,// the pipe name,
            fileAccess,           // read access that allows to set ReadMode to message on lines 114 & 172
            0,                  // sharing: none
            IntPtr.Zero,           // security attributes
            FileMode.Open,      // open existing
            pipeFlags,         // impersonation flags
            IntPtr.Zero);  // template file: null
        if (this.handle.IsInvalid)
        {
            error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"Failed to create file for pipe. Win error: {error}");
        }

        _pipe = new NamedPipeClientStream(PipeDirection.InOut, isAsync: true, isConnected: true, safePipeHandle: handle);
    }

    public async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext socketsHttpConnectionContext, CancellationToken cancellationToken)
    {
        // Reading the first frame that the server sends to unblock the first write that the client will do.
        // The Http2Stream will respond with this preface response at first read.
        // This is the same behavior that http2_client in go. Line 367 t.reader()
        // https://github.com/grpc/grpc-go/blob/master/internal/transport/http2_client.go
        // Start the reader goroutine for incoming message. Each transport has
        // a dedicated goroutine which reads HTTP2 frame from network. Then it
        // dispatches the frame to the corresponding stream entity.
        var b = new byte[32];
        var bytes = await _pipe.ReadAsync(b);
        Console.WriteLine($"Read {bytes} from named pipe to avoid blocking further calls.");
        Memory<byte> prefaceResponse = new Memory<byte>(b, 0, bytes);

        var http2Stream = new Http2Stream(_pipe, prefaceResponse);
        return http2Stream;
    }

    public void Dispose()
    {
        this._pipe.Dispose();
        this.handle.Dispose();
    }
}

public interface IConnectionFactory: IDisposable
{
    ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext socketsHttpConnectionContext, CancellationToken cancellationToken);
}

class Http2Stream : Stream
{
    static object settingsFrameReadLock = new object();
    public Http2Stream(NamedPipeClientStream namedPipeClientStream)
    {
        namedpipeClientStream = namedPipeClientStream;
        this.isFirstRead = true;
    }

    public Http2Stream(NamedPipeClientStream namedPipeClientStream, Memory<byte> prefaceServerResponse)
    {
        namedpipeClientStream = namedPipeClientStream;
        this.prefaceServerResponse = prefaceServerResponse;
        this.isFirstRead = true;
    }

    public override bool CanRead => namedpipeClientStream.CanRead;

    public override bool CanSeek => namedpipeClientStream.CanSeek;

    public override bool CanWrite => namedpipeClientStream.CanWrite;

    public override long Length => namedpipeClientStream.Position;

    public override long Position { get { return this.namedpipeClientStream.Position; } set  { this.namedpipeClientStream.Position = value; } }
    private NamedPipeClientStream namedpipeClientStream { get; }

    private Memory<byte> prefaceServerResponse;

    private bool isFirstRead;

    public override void Flush()
    {
        this.namedpipeClientStream.Flush();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return this.namedpipeClientStream.Read(buffer, offset, count);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        return this.namedpipeClientStream.Seek(offset, origin);
    }

    public override void SetLength(long value)
    {
        this.namedpipeClientStream.SetLength(value);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        this.namedpipeClientStream.Write(buffer, offset, count);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return this.namedpipeClientStream.ReadAsync(buffer, offset, count, cancellationToken);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        /// We do the first read at connection time and when the httpclient calls we respond with that same response.
        if (isFirstRead)
        {
            lock (settingsFrameReadLock)
            {
                if (isFirstRead)
                {
                    this.prefaceServerResponse.CopyTo(buffer);
                    isFirstRead = false;
                    return this.prefaceServerResponse.Length;
                }
            }
        }

        var readBytes = await this.namedpipeClientStream.ReadAsync(buffer, cancellationToken);
        return readBytes;
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return this.namedpipeClientStream.WriteAsync(buffer, offset, count, cancellationToken);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        return this.namedpipeClientStream.WriteAsync(buffer, cancellationToken);
    }
}