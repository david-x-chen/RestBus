﻿#define ENABLE_CHANNELPOOLING

using RabbitMQ.Client;
using RestBus.Common;
using System;
using System.Collections.Concurrent;


namespace RestBus.RabbitMQ.ChannelPooling
{

    internal sealed class AmqpChannelPooler : IDisposable
    {
        readonly IConnection conn;
        InterlockedBoolean _disposed;

#if ENABLE_CHANNELPOOLING

        readonly ConcurrentDictionary<ChannelFlags, ConcurrentQueue<AmqpModelContainer>> _pool = new ConcurrentDictionary<ChannelFlags, ConcurrentQueue<AmqpModelContainer>>();
        static readonly int MODEL_EXPIRY_TIMESPAN = (int)TimeSpan.FromMinutes(5).TotalMilliseconds;

#endif

        public bool IsDisposed
        {
            get { return _disposed.IsTrue; }
        }

        public AmqpChannelPooler(IConnection conn)
        {
            this.conn = conn;
        }

        internal AmqpModelContainer GetModel(ChannelFlags flags)
        {
#if ENABLE_CHANNELPOOLING

            //Search pool for a model:
            AmqpModelContainer model = null;

            //First get the queue
            ConcurrentQueue<AmqpModelContainer> queue;
            if (_pool.TryGetValue(flags, out queue))
            {
                bool retry;
                int tick = Environment.TickCount;

                //Dequeue queue until an unexpired model is found 
                do
                {
                    retry = false;
                    model = null;
                    if (queue.TryDequeue(out model))
                    {
                        if (HasModelExpired(tick, model))
                        {

                            DisposeModel(model); // dispose model
                            retry = true;
                        }
                    }
                }
                while (retry );
            }

            if (model == null)
            {
                //Wasn't found, so create a new one
                model = new AmqpModelContainer(conn.CreateModel(), flags, this);
            }

            return model;


#else
            return new AmqpModelContainer( conn.CreateModel(), flags, this);
#endif

        }

        internal void ReturnModel(AmqpModelContainer modelContainer)
        {
            // NOTE: do not call AmqpModelContainer.Close() here.
            // That method calls this method.

#if ENABLE_CHANNELPOOLING

            if (_disposed.IsTrue || HasModelExpired(Environment.TickCount, modelContainer))
            {
                DisposeModel(modelContainer);
                return;
            }

            //Insert model in pool
            ConcurrentQueue<AmqpModelContainer> queue;
            if (_pool.TryGetValue(modelContainer.Flags, out queue))
            {
                //Found the queue so just enqueue the model
                queue.Enqueue(modelContainer);

                //TODO: AddOrUpdate below performs this lookup so this code here is redundant.
                //Consider removing this code and using an Add factory to eliminate the new queue allocation below
            }
            else
            {
                //Attempt to add a new queue, if a queue doesn't exist and if it does, then add model to queue

                queue = new ConcurrentQueue<AmqpModelContainer>();
                queue.Enqueue(modelContainer);

                _pool.AddOrUpdate(modelContainer.Flags, queue, (f, q) => { q.Enqueue(modelContainer); return q; });
            }


            //It's possible for the disposed flag to be set (and the pool flushed) right after the first _disposed check
            //but before the modelContainer was added, so check again. 
            if (_disposed.IsTrue)
            {
                Flush();
            }

#else
            DisposeModel(modelContainer);
#endif
        }



        public void Dispose()
        {
            _disposed.Set(true);

#if ENABLE_CHANNELPOOLING
            Flush();
#endif
            

        }

#if ENABLE_CHANNELPOOLING
        private void Flush()
        {
            var snapshot = _pool.ToArray();

            ConcurrentQueue<AmqpModelContainer> queue;
            AmqpModelContainer model;
            foreach (var kv in snapshot)
            {
                queue = kv.Value;
                while (queue.TryDequeue(out model))
                {
                    DisposeModel(model);
                }
            }
        }


        private static bool HasModelExpired(int currentTickCount, AmqpModelContainer modelContainer)
        {
            //TickCount wrapped around (so timespan can't be trusted) or model has expired
            return currentTickCount < modelContainer.Created || modelContainer.Created < (currentTickCount - MODEL_EXPIRY_TIMESPAN);
        }

#endif

        private static void DisposeModel(AmqpModelContainer modelContainer)
        {
            if (modelContainer != null && modelContainer.Channel != null)
            {
                try
                {
                    modelContainer.Channel.Dispose();
                }
                catch { }
            }
        }
    }
}
