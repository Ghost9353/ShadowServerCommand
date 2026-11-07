using System.Net;
using System.Net.Sockets;
using System.Text;
using SSC.Infrastructure;
using Xunit;
namespace SSC.IntegrationTests;
public sealed class MinecraftStatusTests
{
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task OpenPortIsDistinguishedFromRespondingMinecraft(bool respond)
    {
        using var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();var port=((IPEndPoint)listener.LocalEndpoint).Port;
        // Leave time for first-use JIT and Windows hosted-runner scheduling. The
        // same deadline still proves a silent open port is not responsive.
        var check=MinecraftStatusProbe.CheckAsync("127.0.0.1",port,true,TimeSpan.FromSeconds(5),CancellationToken.None);
        using var connection=await listener.AcceptTcpClientAsync();
        if(respond)
        {
            var json=Encoding.UTF8.GetBytes("{\"version\":{\"name\":\"test\",\"protocol\":767},\"description\":\"test fixture\"}");
            using var packet=new MemoryStream();MinecraftStatusProbe.WriteVarInt(packet,0);MinecraftStatusProbe.WriteVarInt(packet,json.Length);packet.Write(json);
            using var data=new MemoryStream();MinecraftStatusProbe.WriteVarInt(data,(int)packet.Length);packet.WriteTo(data);await connection.GetStream().WriteAsync(data.ToArray());
        }
        var result=await check;Assert.True(result.Port);Assert.Equal(respond,result.Responsive);
    }
    [Fact] public async Task ClosedPortIsUnavailable()
    {
        using var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();var port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();
        var result=await MinecraftStatusProbe.CheckAsync("127.0.0.1",port,true,TimeSpan.FromSeconds(1),CancellationToken.None);
        Assert.False(result.Port);Assert.Null(result.Responsive);
    }
}
