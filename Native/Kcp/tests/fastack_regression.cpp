// Regression for https://github.com/skywind3000/kcp/pull/467.
// Uses two real KCP endpoints, full-MSS datagrams and receiver-generated ACKs.
#include "ikcp.h"

#include <cstdio>
#include <cstring>
#include <stdexcept>
#include <vector>

typedef std::vector<std::vector<char> > Packets;

static void Require(bool condition, const char *message)
{
    if (!condition)
    {
        throw std::runtime_error(message);
    }
}

static int Capture(const char *data, int length, ikcpcb *, void *user)
{
    Packets *packets = static_cast<Packets *>(user);
    packets->push_back(std::vector<char>(data, data + length));
    return 0;
}

struct Endpoints
{
    ikcpcb *sender;
    ikcpcb *receiver;

    Endpoints(Packets &data, Packets &acks)
        : sender(ikcp_create(1, &data)), receiver(ikcp_create(1, &acks))
    {
    }

    ~Endpoints()
    {
        if (sender != NULL)
        {
            ikcp_release(sender);
        }

        if (receiver != NULL)
        {
            ikcp_release(receiver);
        }
    }
};

static void Deliver(Packets &packets, ikcpcb *peer, int droppedIndex, bool reverse)
{
    for (int i = 0; i < static_cast<int>(packets.size()); i++)
    {
        int index = reverse ? static_cast<int>(packets.size()) - i - 1 : i;
        if (index == droppedIndex)
        {
            continue;
        }

        const std::vector<char> &packet = packets[index];
        Require(ikcp_input(peer, &packet[0], static_cast<long>(packet.size())) == 0,
            "KCP rejected a peer-generated packet");
    }

    packets.clear();
}

static void TestFastAck(bool baseline, bool gameframe, bool reverse, IUINT32 start)
{
    Packets data;
    Packets acks;
    Endpoints endpoints(data, acks);
    ikcpcb *sender = endpoints.sender;
    ikcpcb *receiver = endpoints.receiver;
    Require(sender != NULL && receiver != NULL, "KCP create failed");
    ikcp_setoutput(sender, Capture);
    ikcp_setoutput(receiver, Capture);
    Require(ikcp_nodelay(sender, 1, 10, 2, gameframe ? 0 : 1) == 0, "sender config failed");
    Require(ikcp_nodelay(receiver, 1, 10, 2, gameframe ? 0 : 1) == 0, "receiver config failed");

    if (gameframe)
    {
        Require(ikcp_setmtu(sender, 1150) == 0, "sender MTU failed");
        Require(ikcp_setmtu(receiver, 1150) == 0, "receiver MTU failed");
        ikcp_wndsize(sender, 128, 128);
        ikcp_wndsize(receiver, 128, 128);
        sender->rx_minrto = 30;
        receiver->rx_minrto = 30;

        // Model a mature connection; this is not the initial congestion window.
        sender->cwnd = 128;
        sender->incr = 128 * sender->mss;
    }

    std::vector<char> message(sender->mss);
    for (int batch = 0; batch < 2; batch++)
    {
        int first = batch == 0 ? 0 : 4;
        int last = batch == 0 ? 4 : 7;
        for (int i = first; i < last; i++)
        {
            std::memset(&message[0], i, message.size());
            Require(ikcp_send(sender, &message[0], static_cast<int>(message.size())) ==
                static_cast<int>(message.size()), "KCP send did not accept the complete message");
        }

        ikcp_update(sender, start + batch * 20);
        Require(data.size() == static_cast<size_t>(last - first), "unexpected data packet count");
        for (size_t i = 0; i < data.size(); i++)
        {
            Require(data[i].size() == sender->mtu, "data packet did not fill the MTU");
        }

        Deliver(data, receiver, batch == 0 ? 1 : -1, reverse);
        ikcp_update(receiver, start + batch * 20);
        Require(acks.size() == 1 && acks[0].size() == 3 * 24, "unexpected ACK batch");
        Deliver(acks, sender, -1, false);
    }

    ikcp_update(sender, start + 30);
    Deliver(data, receiver, -1, false);
    int received = 0;
    for (;;)
    {
        int length = ikcp_recv(receiver, &message[0], static_cast<int>(message.size()));
        if (length < 0)
        {
            Require(length == -1 || length == -2, "unexpected receive error");
            break;
        }

        Require(length == static_cast<int>(message.size()), "wrong message length");
        for (int i = 0; i < length; i++)
        {
            Require(message[i] == static_cast<char>(received), "message order/content mismatch");
        }

        received++;
    }

    int expected = baseline && !reverse ? 1 : 7;
    std::printf("%s config=%s reverse=%d start=%u delivered=%d/7 at 30ms\n",
        baseline ? "baseline" : "patched", gameframe ? "gameframe" : "pr",
        static_cast<int>(reverse), static_cast<unsigned>(start), received);
    Require(received == expected, "fast ACK delivery regression");
    Require(sender->xmit == 0, "delivery used RTO instead of fast retransmit");
}

static void TestFixedTick()
{
    Packets data;
    Packets acks;
    Endpoints endpoints(data, acks);
    ikcpcb *sender = endpoints.sender;
    Require(sender != NULL && endpoints.receiver != NULL, "KCP create failed");
    ikcp_setoutput(sender, Capture);
    ikcp_nodelay(sender, 1, 10, 2, 1);
    Require(ikcp_send(sender, "x", 1) == 1, "scheduler test send failed");
    ikcp_update(sender, 0);
    ikcp_update(sender, 195);

    // A delayed update can leave the next RTO earlier than the next flush.
    IUINT32 next = ikcp_check(sender, 195);
    size_t before = data.size();
    ikcp_update(sender, next);
    Require(next == 200 && data.size() == before && ikcp_check(sender, next) == next,
        "check/update boundary was not reproduced");

    // The next scheduled tick advances time instead of retrying the same due entry.
    ikcp_update(sender, 205);
    Require(data.size() == before + 1 && ikcp_check(sender, 205) > 205,
        "fixed tick did not advance retransmission");
    std::puts("scheduler: repeated due check reproduced; next 10ms tick makes progress");
}

int main(int argc, char **argv)
{
    try
    {
        Require(argc == 2 && (std::strcmp(argv[1], "--baseline") == 0 ||
            std::strcmp(argv[1], "--patched") == 0), "expected --baseline or --patched");
        bool baseline = std::strcmp(argv[1], "--baseline") == 0;
        for (int config = 0; config < 2; config++)
        {
            for (int reverse = 0; reverse < 2; reverse++)
            {
                TestFastAck(baseline, config != 0, reverse != 0, 1000);
                TestFastAck(baseline, config != 0, reverse != 0, 0xfffffff0u);
            }
        }

        TestFixedTick();
        return 0;
    }
    catch (const std::exception &error)
    {
        std::fprintf(stderr, "%s\n", error.what());
        return 1;
    }
}
