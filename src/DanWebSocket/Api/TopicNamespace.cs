using System;
using System.Collections.Generic;

namespace DanWebSocket.Api
{
    /// <summary>
    /// Topic event registry for subscribe/unsubscribe callbacks.
    /// </summary>
    public class TopicNamespace
    {
        internal readonly List<Action<DanWebSocketSession, TopicHandle>> _onSubscribeCbs = new List<Action<DanWebSocketSession, TopicHandle>>();
        internal readonly List<Action<DanWebSocketSession, TopicHandle>> _onUnsubscribeCbs = new List<Action<DanWebSocketSession, TopicHandle>>();

        public void OnSubscribe(Action<DanWebSocketSession, TopicHandle> cb)
        {
            _onSubscribeCbs.Add(cb);
        }

        public void OnUnsubscribe(Action<DanWebSocketSession, TopicHandle> cb)
        {
            _onUnsubscribeCbs.Add(cb);
        }
    }
}
