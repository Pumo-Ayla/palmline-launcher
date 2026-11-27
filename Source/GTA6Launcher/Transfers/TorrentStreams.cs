using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MonoTorrent;
using MonoTorrent.Client;

// Archive readers can enumerate all volumes before reading the first byte.
// MonoTorrent permits a single active verified stream per torrent.
internal sealed class TorrentStreams : IDisposable
{
    readonly TorrentManager manager;
    readonly CancellationToken token;
    readonly CancellationTokenSource readTimeout;
    Stream current;
    ITorrentManagerFile active;
    public TorrentStreams(TorrentManager manager,CancellationToken token) {
        this.manager=manager; this.token=token;
        readTimeout=CancellationTokenSource.CreateLinkedTokenSource(token);
    }
    public Stream Volume(ITorrentManagerFile file) => new BufferedStream(new VolumeStream(this,file),65536);
    public void Dispose() { current?.Dispose(); readTimeout.Dispose(); }
    T WaitForData<T>(Task<T> pending) {
        if(pending.IsCompleted) return pending.GetAwaiter().GetResult();
        readTimeout.CancelAfter(TimeSpan.FromSeconds(90));
        try { return pending.WaitAsync(readTimeout.Token).GetAwaiter().GetResult(); }
        catch(OperationCanceledException) when(!token.IsCancellationRequested && readTimeout.IsCancellationRequested) {
            throw new TimeoutException("PEER_TIMEOUT");
        } finally { readTimeout.CancelAfter(Timeout.InfiniteTimeSpan); }
    }
    int Read(ITorrentManagerFile file,long position,byte[] bytes,int offset,int length) {
        token.ThrowIfCancellationRequested();
        if(active!=file) {
            current?.Dispose();
            current=WaitForData(manager.StreamProvider.CreateStreamAsync(file,false,readTimeout.Token));
            active=file;
        }
        if(current.Position!=position) current.Position=position;
        return WaitForData(current.ReadAsync(bytes,offset,length,readTimeout.Token));
    }
    sealed class VolumeStream : Stream {
        readonly TorrentStreams owner;
        readonly ITorrentManagerFile file;
        long position;
        public VolumeStream(TorrentStreams owner,ITorrentManagerFile file) { this.owner=owner; this.file=file; }
        public override long Length=>file.Length;
        public override bool CanRead=>true;
        public override bool CanSeek=>true;
        public override bool CanWrite=>false;
        public override long Position { get=>position; set=>Seek(value,SeekOrigin.Begin); }
        public override long Seek(long offset,SeekOrigin origin) {
            long next=checked(offset+(origin==SeekOrigin.End ? Length : origin==SeekOrigin.Current ? position : 0));
            if(next<0) throw new IOException("Negative file position.");
            return position=next;
        }
        public override int Read(byte[] buffer,int offset,int count) {
            if(position>=Length || count==0) return 0;
            int n=owner.Read(file,position,buffer,offset,(int)Math.Min(count,Length-position));
            position+=n;
            return n;
        }
        public override void Flush() { }
        public override void SetLength(long length)=>throw new NotSupportedException();
        public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
    }
}
