using System.Net.Sockets;
using System.Text;
using System.Text.Json;
namespace SSC.Infrastructure;
public static class MinecraftStatusProbe
{
    public static async Task<(bool Port,bool? Responsive)> CheckAsync(string host,int port,bool statusEnabled,TimeSpan timeout,CancellationToken ct)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(timeout);
        using var tcp=new TcpClient();var connected=false;
        try
        {
            await tcp.ConnectAsync(host,port,deadline.Token);connected=true;
            if(!statusEnabled)return(true,null);
            await using var stream=tcp.GetStream();
            using var handshake=new MemoryStream();
            WriteVarInt(handshake,0);WriteVarInt(handshake,767); // Java 1.21 / 1.21.1 status handshake
            var address=Encoding.UTF8.GetBytes(host);WriteVarInt(handshake,address.Length);handshake.Write(address);
            handshake.WriteByte((byte)(port>>8));handshake.WriteByte((byte)port);WriteVarInt(handshake,1);
            using var request=new MemoryStream();WriteVarInt(request,(int)handshake.Length);handshake.WriteTo(request);request.Write([1,0]);
            await stream.WriteAsync(request.ToArray(),deadline.Token);
            var length=await ReadVarIntAsync(stream,deadline.Token);
            if(length is < 2 or > 1048576)return(true,false);
            var buffer=new byte[length];await stream.ReadExactlyAsync(buffer,deadline.Token);
            using var packet=new MemoryStream(buffer,false);
            if(await ReadVarIntAsync(packet,deadline.Token)!=0)return(true,false);
            var jsonLength=await ReadVarIntAsync(packet,deadline.Token);
            if(jsonLength<=0 || jsonLength!=packet.Length-packet.Position)return(true,false);
            using var json=JsonDocument.Parse(buffer.AsMemory((int)packet.Position,jsonLength));
            return(true,json.RootElement.ValueKind==JsonValueKind.Object && json.RootElement.TryGetProperty("version",out _) && json.RootElement.TryGetProperty("description",out _));
        }
        catch(Exception ex) when(ex is SocketException or IOException or OperationCanceledException or JsonException or InvalidDataException)
        { ct.ThrowIfCancellationRequested();return(connected,connected && statusEnabled ? false : null); }
    }
    public static void WriteVarInt(Stream stream,int value)
    {
        var remaining=(uint)value;
        do { var part=(byte)(remaining&0x7f);remaining>>=7;stream.WriteByte(remaining==0?part:(byte)(part|0x80)); } while(remaining!=0);
    }
    private static async Task<int> ReadVarIntAsync(Stream stream,CancellationToken ct)
    {
        var value=0;var buffer=new byte[1];
        for(var shift=0;shift<35;shift+=7)
        {
            await stream.ReadExactlyAsync(buffer,ct);var b=buffer[0];
            if(shift==28 && (b&0xf0)!=0)throw new InvalidDataException("Invalid VarInt");
            value|=(b&0x7f)<<shift;if((b&0x80)==0)return value;
        }
        throw new InvalidDataException("Overlong VarInt");
    }
}
