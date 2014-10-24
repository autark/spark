﻿using Hl7.Fhir.Model;
using Hl7.Fhir.Rest;
using Hl7.Fhir.Serialization;
using MongoDB.Bson;
using MongoDB.Driver;
using Spark.Config;
using Spark.Store;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MonQ = MongoDB.Driver.Builders;

namespace Spark.Core
{

    public interface IEntryStore
    {
        BundleEntry Get(Uri key);
        IEnumerable<BundleEntry> Get(IEnumerable<Uri> keys);
        BundleEntry Add(BundleEntry entry, Guid? transaction = null);
        BundleEntry Add(IEnumerable<BundleEntry> entries, Guid? transaction = null);
    }

    public interface ITagStore
    {
        IEnumerable<Tag> Tags();
        IEnumerable<Tag> Tags(string collection);
        IEnumerable<Uri> Find(params Tag[] tags);
    }

    public interface ISnapshotStore
    {
        void AddSnapshot(Snapshot snapshot);
        void GetSnapshot(string snapshotkey);
    }

    public interface IKeyStore
    {
        IEnumerable<Uri> List(string resource, DateTime? since = null);
        IEnumerable<Uri> History(string resource, DateTime? since = null);
        IEnumerable<Uri> History(Uri key, DateTime? since = null);
        IEnumerable<Uri> History(DateTime? since = null);
    }


    public class NewMongoFhirStore : IKeyStore, IEntryStore
    {
        MongoDatabase database;
        MongoCollection<BsonDocument> collection;

        public NewMongoFhirStore(MongoDatabase database)
        {
            this.database = database;
            this.collection = database.GetCollection(Collection.RESOURCE);
        }

        public IEnumerable<Uri> List(string resource, DateTime? since = null)
        {
            var clauses = new List<IMongoQuery>();

            clauses.Add(MonQ.Query.EQ(Collection.RESOURCE, resource));
            if (since != null)   
                clauses.Add(MonQ.Query.GT(Field.VERSIONDATE, BsonDateTime.Create(since)));
            clauses.Add(MonQ.Query.EQ(Field.STATE, Value.CURRENT));
            clauses.Add(MonQ.Query.NE(Field.ENTRYTYPE, typeof(DeletedEntry).Name));

            return FetchKeys(clauses);
        }

        public IEnumerable<Uri> History(string resource, DateTime? since = null)
        {
            var clauses = new List<IMongoQuery>();

            clauses.Add(MonQ.Query.EQ(Collection.RESOURCE, resource));
            if (since != null)
                clauses.Add(MonQ.Query.GT(Field.VERSIONDATE, BsonDateTime.Create(since)));

            return FetchKeys(clauses);
        }

        public IEnumerable<Uri> History(Uri key, DateTime? since = null)
        {
            var clauses = new List<IMongoQuery>();

            clauses.Add(MonQ.Query.EQ(Field.ID, key.ToString()));
            if (since != null) 
                clauses.Add(MonQ.Query.GT(Field.VERSIONDATE, BsonDateTime.Create(since)));

            return FetchKeys(clauses);
        }

        public IEnumerable<Uri> History(DateTime? since = null)
        {
            var clauses = new List<IMongoQuery>();
            if (since != null) 
                clauses.Add(MonQ.Query.GT(Field.VERSIONDATE, BsonDateTime.Create(since)));

            return FetchKeys(clauses);
        }


        public BundleEntry Get(Uri key)
        {
            BsonDocument document = collection.FindOne(MonQ.Query.EQ(Field.RECORDID, key.ToString()));
            if (document != null)
            {
                BundleEntry entry = BsonToBundleEntry(document);
                return entry;
            }
            else
            {
                return null;
            }
        }

        public IEnumerable<BundleEntry> Get(IEnumerable<Uri> keys)
        {
            IEnumerable<BsonValue> ids = keys.Select(k => (BsonValue)k.ToString());
            MongoCursor<BsonDocument> cursor = collection.Find(MonQ.Query.In(Field.ID, ids));

            foreach (BsonDocument document in cursor)
            {
                BundleEntry entry = BsonToBundleEntry(document);
                yield return entry;
            }
            
        }

        public BundleEntry Add(BundleEntry entry, Guid? transaction = null)
        {
            
        }

        public BundleEntry Add(IEnumerable<BundleEntry> entries, Guid? transaction = null)
        {
            throw new NotImplementedException();
        }


        public static class Collection
        {
            public const string RESOURCE = "resources";
            public const string COUNTERS = "counters";
            public const string SNAPSHOT = "snapshots";
        }

        public static class Field
        {
            public const string STATE = "@state";
            public const string VERSIONDATE = "@versionDate";
            public const string ENTRYTYPE = "@entryType";
            public const string COLLECTION = "@collection";
            public const string BATCHID = "@batchId";

            public const string ID = "id";
            public const string RECORDID = "_id"; // SelfLink is re-used as the Mongo key
        }

        public static class Value
        {
            public const string CURRENT = "current";
            public const string SUPERCEDED = "superceded";
        }

        public IEnumerable<Uri> FetchKeys(IMongoQuery query)
        {
            MongoCursor<BsonDocument> cursor = collection
                .Find(query)
                .SetSortOrder(MonQ.SortBy.Descending(Field.VERSIONDATE))
                .SetFields(MonQ.Fields.Include(Field.RECORDID));

            return cursor.Select(doc => new Uri(doc.GetValue(Field.RECORDID).AsString));
        }

        public IEnumerable<Uri> FetchKeys(IEnumerable<IMongoQuery> clauses)
        {
            IMongoQuery query = MonQ.Query.And(clauses);
            return FetchKeys(query);
        }

        private static BundleEntry BsonToBundleEntry(BsonDocument document)
        {
            try
            {
                // Remove our storage metadata before deserializing
                RemoveMetadata(document);
                string json = document.ToJson();
                BundleEntry entry = FhirParser.ParseBundleEntryFromJson(json);
                return entry;
            }
            catch (Exception inner)
            {
                throw new InvalidOperationException("Cannot parse MongoDb's json into a feed entry: ", inner);
            }

        }

        private static void RemoveMetadata(BsonDocument document)
        {
            document.Remove(Field.VERSIONDATE);
            document.Remove(Field.STATE);
            document.Remove(Field.RECORDID);
            document.Remove(Field.ENTRYTYPE);
            document.Remove(Field.COLLECTION);
            document.Remove(Field.BATCHID);
        }
    }




    


    public class MongoFhirStore3 : IFhirStore
    {
        MongoDatabase database; // todo: set
        Transaction transaction;
        //private const string BSON_BLOBID_MEMBER = "@blobId";
        private const string BSON_STATE_MEMBER = "@state";
        private const string BSON_VERSIONDATE_MEMBER = "@versionDate";
        private const string BSON_ENTRY_TYPE_MEMBER = "@entryType";
        public const string BSON_COLLECTION_MEMBER = "@collection";
        private const string BSON_BATCHID_MEMBER = "@batchId";

        private const string BSON_ID_MEMBER = "id";
        private const string BSON_RECORDID_MEMBER = "_id";      // SelfLink is re-used as the Mongo key
        // private const string BSON_CONTENT_MEMBER = "content";

        private const string BSON_STATE_CURRENT = "current";
        private const string BSON_STATE_SUPERCEDED = "superceded";

        public const string RESOURCE_COLLECTION = "resources";
        private const string COUNTERS_COLLECTION = "counters";
        private const string SNAPSHOT_COLLECTION = "snapshots";
        private MongoCollection<BsonDocument> collection;

        public MongoFhirStore3(MongoDatabase database)
        {
            this.database = database;
            transaction = new Transaction(this.ResourceCollection);
            this.collection = database.GetCollection(RESOURCE_COLLECTION);
        }


        /// <summary>
        /// Retrieves an Entry by its id. This includes content and deleted entries.
        /// </summary>
        /// <param name="url"></param>
        /// <returns>An entry, including full content, or null if there was no entry with the given id</returns>
        public BundleEntry FindEntryById(Uri url)
        {
            var found = transaction.ReadCurrent(url.ToString());
            if (found == null) return null;

            return reconstituteBundleEntry(found, fetchContent: true);
        }


        public IEnumerable<BundleEntry> FindEntriesById(IEnumerable<Uri> ids)
        {
            var queries = new List<IMongoQuery>();
            var keys = ids.Select(uri => (BsonValue)uri.ToString());

            queries.Add(MonQ.Query.EQ(BSON_STATE_MEMBER, BSON_STATE_CURRENT));
            queries.Add(MonQ.Query.In(BSON_ID_MEMBER, keys));

            var documents = ResourceCollection.Find(MonQ.Query.And(queries));
            return documents.Select(doc => reconstituteBundleEntry(doc, fetchContent: true));
        }


        /*
        private IMongoQuery makeFindCurrentByIdQuery(string url)
        {
            return MonQ.Query.And(
                    MonQ.Query.EQ(BSON_ID_MEMBER, url),
                    MonQ.Query.EQ(BSON_STATE_MEMBER, BSON_STATE_CURRENT)
                    );
        }

        private IMongoQuery makeFindCurrentByIdQuery(IEnumerable<Uri> urls)
        {
            return MonQ.Query.And(
                    MonQ.Query.In(BSON_ID_MEMBER, urls.Select(url => BsonValue.Create(url.ToString()))),
                    MonQ.Query.EQ(BSON_STATE_MEMBER, BSON_STATE_CURRENT)
                    );
        }
        
        private BsonDocument findCurrentDocumentById(string url)
        {
            var coll = getResourceCollection();
           IMongoQuery query = MonQ.Query.And(
                    MonQ.Query.EQ(BSON_ID_MEMBER, url),
                    MonQ.Query.EQ(BSON_STATE_MEMBER, BSON_STATE_CURRENT)
                    );
            return coll.FindOne(makeFindCurrentByIdQuery(url));
        }
        */

        /// <summary>
        /// Retrieves a specific version of an Entry. Includes content and deleted entries.
        /// </summary>
        /// <param name="url"></param>
        /// <returns>An entry, including full content, or null if there was no entry with the given id</returns>
        public BundleEntry FindVersionByVersionId(Uri url)
        {
            var coll = getResourceCollection();

            var found = coll.FindOne(MonQ.Query.EQ(BSON_RECORDID_MEMBER, url.ToString()));

            if (found == null) return null;

            return reconstituteBundleEntry(found, fetchContent: true);
        }

        public IEnumerable<BundleEntry> FindByVersionIds(IEnumerable<Uri> entryVersionIds)
        {
            var coll = getResourceCollection();

            var keys = entryVersionIds.Select(uri => (BsonValue)uri.ToString());
            var query = MonQ.Query.In(BSON_RECORDID_MEMBER, keys);
            var entryDocs = coll.Find(query);

            // Unfortunately, Query.IN returns the entries out of order with respect to the set of id's
            // passed to it as a parameter...we have to resort.
            var sortedDocs = entryDocs.OrderBy(doc => doc[BSON_RECORDID_MEMBER].ToString(),
                    new SnapshotSorter(entryVersionIds.Select(vi => vi.ToString())));

            return sortedDocs.Select(doc => reconstituteBundleEntry(doc, fetchContent: true));
        }


        private class SnapshotSorter : IComparer<string>
        {
            Dictionary<string, int> keyPositions = new Dictionary<string, int>();

            public SnapshotSorter(IEnumerable<string> keys)
            {
                int index = 0;
                foreach (var key in keys)
                {
                    keyPositions.Add(key, index);
                    index += 1;
                }
            }

            public int Compare(string a, string b)
            {
                return keyPositions[a] - keyPositions[b];
            }
        }

        public IEnumerable<BundleEntry> ListCollection(string collectionName, bool includeDeleted = false,
                                DateTimeOffset? since = null, int limit = 100)
        {
            return findAll(limit, since, onlyCurrent: true, includeDeleted: includeDeleted, collection: collectionName);
        }

        public IEnumerable<BundleEntry> ListVersionsInCollection(string collectionName, DateTimeOffset? since = null,
                            int limit = 100)
        {
            return findAll(limit, since, onlyCurrent: false, includeDeleted: true, collection: collectionName);
        }

        public IEnumerable<BundleEntry> ListVersionsById(Uri url, DateTimeOffset? since = null,
                                    int limit = 100)
        {
            return findAll(limit, since, onlyCurrent: false, includeDeleted: true, id: url);
        }

        public IEnumerable<BundleEntry> ListVersions(DateTimeOffset? since = null, int limit = 100)
        {
            return findAll(limit, since, onlyCurrent: false, includeDeleted: true);
        }


        private IEnumerable<BsonValue> collectBsonKeys(IMongoQuery query, IMongoSortBy sortby = null)
        {
            MongoCursor<BsonDocument> cursor = collection.Find(query).SetFields(BSON_RECORDID_MEMBER);

            if (sortby != null)
                cursor = cursor.SetSortOrder(sortby);

            return cursor.Select(doc => doc.GetValue(BSON_RECORDID_MEMBER));
        }

        private IEnumerable<Uri> collectKeys(IMongoQuery query, IMongoSortBy sortby = null)
        {
            return collectBsonKeys(query, sortby).Select(key => new Uri(key.ToString(), UriKind.Relative));
        }

        public ICollection<Uri> HistoryKeys(DateTimeOffset? since = null)
        {
            DateTime? mongoSince = convertDateTimeOffsetToDateTime(since);
            var queries = new List<IMongoQuery>();

            if (mongoSince != null)
                queries.Add(MonQ.Query.GT(BSON_VERSIONDATE_MEMBER, BsonDateTime.Create(mongoSince)));

            IMongoQuery query = MonQ.Query.And(queries);

            IMongoSortBy sortby = MonQ.SortBy.Descending(BSON_VERSIONDATE_MEMBER);
            return collectKeys(query, sortby).ToList();
        }

        private IEnumerable<BundleEntry> findAll(int limit, DateTimeOffset? since, bool onlyCurrent, bool includeDeleted,
                                    Uri id = null, string collection = null)
        {
            DateTime? mongoSince = convertDateTimeOffsetToDateTime(since);       // Stored in mongo, corrected for Utc

            var coll = getResourceCollection();

            var queries = new List<IMongoQuery>();

            if (mongoSince != null)
                queries.Add(MonQ.Query.GT(BSON_VERSIONDATE_MEMBER, BsonDateTime.Create(mongoSince)));
            if (onlyCurrent)
                queries.Add(MonQ.Query.EQ(BSON_STATE_MEMBER, BSON_STATE_CURRENT));
            if (!includeDeleted)
                queries.Add(MonQ.Query.NE(BSON_ENTRY_TYPE_MEMBER, typeof(DeletedEntry).Name));
            if (id != null)
                queries.Add(MonQ.Query.EQ(BSON_ID_MEMBER, id.ToString()));
            if (collection != null)
                queries.Add(MonQ.Query.EQ(BSON_COLLECTION_MEMBER, collection));

            MongoCursor<BsonDocument> cursor = null;

            if (queries.Count > 0)
                cursor = coll.Find(MonQ.Query.And(queries));
            else
                cursor = coll.FindAll();

            // Get a subset of the list, and don't include the Content field (where the resource data is)
            var result = cursor
                    .SetFields(MonQ.Fields.Exclude("Content"))
                    .SetSortOrder(MonQ.SortBy.Descending(BSON_VERSIONDATE_MEMBER))
                    .Take(limit);

            // Return the entries without Resource or binary content. These can be fetched later
            // using the other calls.
            return result.Select(doc => reconstituteBundleEntry(doc, fetchContent: false));
        }

        //private BundleEntry clone(BundleEntry entry)
        //{
        //    ErrorList err = new ErrorList();

        //    var xml = FhirSerializer.SerializeBundleEntryToXml(entry);
        //    var result = FhirParser.ParseBundleEntryFromXml(xml, err);

        //    if (err.Count > 0)
        //        throw new InvalidOperationException("Unexpected parse error while cloning: " + err.ToString());

        //    return result;
        //}

        public BundleEntry AddEntry(BundleEntry entry, Guid? batchId = null)
        {
            if (entry == null) throw new ArgumentNullException("entry");

            return AddEntries(new BundleEntry[] { entry }, batchId).FirstOrDefault();
        }

        public IEnumerable<BundleEntry> AddEntries(IEnumerable<BundleEntry> entries, Guid? batchId = null)
        {
            if (entries == null) throw new ArgumentNullException("entries");

            if (entries.Count() == 0) return entries;   // return an empty set

            if (entries.Any(entry => entry.Id == null))
                throw new ArgumentException("All entries must have an entry id");

            if (entries.Any(entry => entry.Links.SelfLink == null))
                throw new ArgumentException("All entries must have a selflink");

            foreach (var entry in entries)
                updateTimestamp(entry);

            save(entries, batchId);

            return entries;
        }

        private static void updateTimestamp(BundleEntry entry)
        {
            if (entry is DeletedEntry)
                ((DeletedEntry)entry).When = DateTimeOffset.Now.ToUniversalTime();
            else
                ((ResourceEntry)entry).LastUpdated = DateTimeOffset.Now.ToUniversalTime();
        }


        public void ReplaceEntry(ResourceEntry entry, Guid? batchId = null)
        {
            if (entry == null) throw new ArgumentNullException("entry");
            if (entry.SelfLink == null) throw new ArgumentException("entry.SelfLink");

            var query = MonQ.Query.EQ(BSON_RECORDID_MEMBER, entry.SelfLink.ToString());

            if (batchId == null) batchId = Guid.NewGuid();
            updateTimestamp(entry);

            var doc = entryToBsonDocument(entry);
            doc[BSON_STATE_MEMBER] = BSON_STATE_CURRENT;
            doc[BSON_BATCHID_MEMBER] = batchId.ToString();

            var coll = getResourceCollection();

            coll.Remove(query);
            coll.Save(doc);
        }

        // This used to be an anonymous function. Should it be merged with entryToBsonDocument ? /mh
        private BsonDocument createDocFromEntry(BundleEntry entry, Guid batchId)
        {
            var doc = entryToBsonDocument(entry);

            doc[BSON_STATE_MEMBER] = BSON_STATE_CURRENT;
            doc[BSON_BATCHID_MEMBER] = batchId.ToString();

            return doc;
        }

        private bool isQuery(BundleEntry entry)
        {
            if (entry is ResourceEntry)
            {
                return !((entry as ResourceEntry).Resource is Query);
            }
            else return true;
        }

        /*
        private void markSuperceded(IEnumerable<Uri> list)
        {
            var query = makeFindCurrentByIdQuery(list);
            ResourceCollection.Update(query, MonQ.Update.Set(BSON_STATE_MEMBER, BSON_STATE_SUPERCEDED), UpdateFlags.Multi);
        }

        private void unmarkSuperceded(IEnumerable<Uri> list)
        {
            // todo: BUG - this is not going to work. because "makeFindCurrentByIdQuery" assumes a BSON_STATE_CURRENT!!!
            ResourceCollection.Update(
                    makeFindCurrentByIdQuery(list),
                    MonQ.Update.Set(BSON_STATE_MEMBER, BSON_STATE_CURRENT), UpdateFlags.Multi);
        }
        */

        private void storeBinaryContents(IEnumerable<BundleEntry> entries)
        {
            foreach (BundleEntry entry in entries)
            {
                if (entry is ResourceEntry<Binary>)
                {
                    var be = (ResourceEntry<Binary>)entry;
                    externalizeBinaryContents(be);
                }
            }
        }

        private void clearBinaryContents(IEnumerable<BundleEntry> entries)
        {
            foreach (BundleEntry entry in entries)
            {
                if (entry is ResourceEntry<Binary>)
                {
                    var be = (ResourceEntry<Binary>)entry;
                    be.Resource.Content = null;
                }
            }
        }

        private void maximizeBinaryContents(IEnumerable<BundleEntry> entries)
        {
            int max = Settings.MaxBinarySize;
            foreach (BundleEntry entry in entries)
            {
                if (entry is ResourceEntry<Binary>)
                {
                    var be = (ResourceEntry<Binary>)entry;
                    int size = be.Resource.Content.Length;
                    if (size > max)
                    {
                        throw new SparkException(string.Format("The maximum size ({0}) for binaries was exceeded. Actual size: {1}", max, size));
                    }
                }
            }
        }

        private void TestTransactionTestException(List<BundleEntry> list)
        {
            // If a patient contains a birth date equal to the birth date of Bach, an exception is thrown
            // This is soleley for the purpose of testing transactions
            foreach (BundleEntry entry in list)
            {
                if (entry is ResourceEntry<Patient>)
                {
                    Patient p = ((ResourceEntry<Patient>)entry).Resource;
                    // Bach's birthdate on the Julian calendar.
                    if (p.BirthDate == "16850331")
                    {
                        throw new Exception("Transaction test exception thrown");
                    }
                }
            }
        }

        /// <summary>
        /// Saves a set of entries to the store, marking existing entries with the
        /// same entry id as superceded.
        /// </summary>
        private void save(IEnumerable<BundleEntry> entries, Guid? batchId = null)
        {
            Guid _batchId = batchId ?? Guid.NewGuid();

            List<BundleEntry> _entries = entries.Where(e => isQuery(e)).ToList();

            maximizeBinaryContents(_entries);

            if (Config.Settings.UseS3)
            {
                storeBinaryContents(_entries);
                clearBinaryContents(_entries);
            }

            List<BsonDocument> docs = _entries.Select(e => createDocFromEntry(e, _batchId)).ToList();
            IEnumerable<Uri> idlist = _entries.Select(e => e.Id);

            try
            {
                transaction.Begin();
                transaction.InsertBatch(docs);

                // TestTransactionTestException(entries.ToList());
                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        private IBlobStorage getBlobStorage()
        {
            return DependencyCoupler.Inject<IBlobStorage>();
        }

        private void externalizeBinaryContents(ResourceEntry<Binary> entry)
        {
            if (entry.Resource.ContentType == null)
                throw new ArgumentException("Entry is a binary entry with content, but the entry does not supply a mediatype");

            if (entry.Resource.Content != null)
            {
                using (var blobStore = getBlobStorage())
                {
                    if (blobStore != null)
                    {
                        var blobId = calculateBlobName(entry.Links.SelfLink);
                        blobStore.Open();
                        blobStore.Store(blobId, new MemoryStream(entry.Resource.Content));
                    }
                }
            }
        }

        public void PurgeBatch(Guid batchId)
        {
            var coll = getResourceCollection();
            var batchQry = MonQ.Query.EQ(BSON_BATCHID_MEMBER, batchId.ToString());

            var batchMembers = coll.Find(batchQry)
                    .SetFields(MonQ.Fields.Include(BSON_RECORDID_MEMBER))
                    .Select(doc => calculateBlobName(new Uri(doc[BSON_RECORDID_MEMBER].ToString())));

            // When using Amazon S3, remove batch from there as well
            if (Config.Settings.UseS3)
            {
                using (var blobStore = getBlobStorage())
                {
                    if (blobStore != null)
                    {
                        blobStore.Open();
                        blobStore.Delete(batchMembers);
                        blobStore.Close();
                    }
                }
            }

            coll.Remove(MonQ.Query.EQ(BSON_BATCHID_MEMBER, batchId.ToString()));
        }

        private DateTime? convertDateTimeOffsetToDateTime(DateTimeOffset? date)
        {
            return date != null ? date.Value.UtcDateTime : (DateTime?)null;
        }

        private BsonDocument entryToBsonDocument(BundleEntry entry)
        {
            var docJson = FhirSerializer.SerializeBundleEntryToJson(entry);
            var doc = BsonDocument.Parse(docJson);

            doc[BSON_RECORDID_MEMBER] = entry.Links.SelfLink.ToString();
            doc[BSON_ENTRY_TYPE_MEMBER] = getEntryTypeFromInstance(entry);
            doc[BSON_COLLECTION_MEMBER] = new ResourceIdentity(entry.Id).Collection;

            if (entry is ResourceEntry)
            {
                doc[BSON_VERSIONDATE_MEMBER] = convertDateTimeOffsetToDateTime(((ResourceEntry)entry).LastUpdated);
                //                doc[BSON_VERSIONDATE_MEMBER_ISO] = Util.FormatIsoDateTime(((ContentEntry)entry).LastUpdated.Value.ToUniversalTime());
            }
            if (entry is DeletedEntry)
            {
                doc[BSON_VERSIONDATE_MEMBER] = convertDateTimeOffsetToDateTime(((DeletedEntry)entry).When);
                //                doc[BSON_VERSIONDATE_MEMBER_ISO] = Util.FormatIsoDateTime(((DeletedEntry)entry).When.Value.ToUniversalTime());
            }

            return doc;
        }

        private string getEntryTypeFromInstance(BundleEntry entry)
        {
            if (entry is DeletedEntry)
                return "DeletedEntry";
            else if (entry is ResourceEntry)
                return "ResourceEntry";
            else
                throw new ArgumentException("Unsupported BundleEntry type: " + entry.GetType().Name);
        }

        private string calculateBlobName(Uri selflink)
        {
            var rl = new ResourceIdentity(selflink);

            return rl.Collection + "/" + rl.Id + "/" + rl.VersionId;
        }

        public void StoreSnapshot(Snapshot snap)
        {
            var coll = database.GetCollection(SNAPSHOT_COLLECTION);

            coll.Save<Snapshot>(snap);
        }

        public Snapshot GetSnapshot(string snapshotId)
        {
            var coll = database.GetCollection(SNAPSHOT_COLLECTION);

            return coll.FindOneByIdAs<Snapshot>(snapshotId);
        }

        public IEnumerable<Tag> ParseToTags(IEnumerable<BsonValue> items)
        {
            List<Tag> tags = new List<Tag>();
            foreach (BsonDocument item in items)
            {
                Tag tag = new Tag(
                    item["term"].AsString,
                    new Uri(item["scheme"].AsString),
                    item["label"].AsString);

                tags.Add(tag);
            }
            return tags;
        }

        public IEnumerable<Tag> ListTagsInServer()
        {
            IEnumerable<BsonValue> items = ResourceCollection.Distinct("category");
            return ParseToTags(items);
        }

        public IEnumerable<Tag> ListTagsInCollection(string collection)
        {
            IMongoQuery query = MonQ.Query.EQ(BSON_COLLECTION_MEMBER, collection);
            IEnumerable<BsonValue> items = ResourceCollection.Distinct("category", query);
            return ParseToTags(items);
        }

        public int GenerateNewIdSequenceNumber()
        {

            var coll = database.GetCollection(COUNTERS_COLLECTION);

            var resourceIdQuery = MonQ.Query.EQ("_id", "resourceId");
            var newId = coll.FindAndModify(resourceIdQuery, null, MonQ.Update.Inc("last", 1), true, true);

            return newId.ModifiedDocument["last"].AsInt32;
        }

        public void EnsureNextSequenceNumberHigherThan(int seq)
        {
            var counters = database.GetCollection(COUNTERS_COLLECTION);

            if (counters.FindOne(MonQ.Query.EQ("_id", "resourceId")) == null)
            {
                counters.Insert(
                    new BsonDocument(new List<BsonElement>()
                    {
                        new BsonElement("_id", "resourceId"),
                        new BsonElement("last", seq)
                    }
                ));
            }
            else
            {
                var resourceIdQuery =
                    MonQ.Query.And(MonQ.Query.EQ("_id", "resourceId"), MonQ.Query.LTE("last", seq));

                counters.FindAndModify(resourceIdQuery, null, MonQ.Update.Set("last", seq));
            }
        }

        public int GenerateNewVersionSequenceNumber()
        {
            var coll = database.GetCollection(COUNTERS_COLLECTION);

            var resourceIdQuery = MonQ.Query.EQ("_id", "versionId");
            var newId = coll.FindAndModify(resourceIdQuery, null, MonQ.Update.Inc("last", 1), true, true);

            return newId.ModifiedDocument["last"].AsInt32;
        }

        // Drops all collections, including the special 'counters' collection for generating ids,
        // AND the binaries stored at Amazon S3
        private void EraseData()
        {
            // Don't try this at home
            var collectionsToDrop = new string[] { RESOURCE_COLLECTION, COUNTERS_COLLECTION, SNAPSHOT_COLLECTION };

            foreach (var collName in collectionsToDrop)
            {
                database.DropCollection(collName);
            }

            // When using Amazon S3, remove blobs from there as well
            if (Config.Settings.UseS3)
            {
                using (var blobStorage = getBlobStorage())
                {
                    if (blobStorage != null)
                    {
                        blobStorage.Open();
                        blobStorage.DeleteAll();
                        blobStorage.Close();
                    }
                }
            }
        }

        private void EnsureIndices()
        {
            var coll = getResourceCollection();

            coll.EnsureIndex(BSON_STATE_MEMBER, BSON_ENTRY_TYPE_MEMBER, BSON_COLLECTION_MEMBER);
            coll.EnsureIndex(BSON_ID_MEMBER, BSON_STATE_MEMBER);

            // Should support ListVersions() and ListVersionsInCollection()
            var versionKeys = MonQ.IndexKeys.Descending(BSON_VERSIONDATE_MEMBER).Ascending(BSON_COLLECTION_MEMBER);
            coll.EnsureIndex(versionKeys);

            //            versionKeys = IndexKeys.Descending(BSON_VERSIONDATE_MEMBER_ISO).Ascending(BSON_COLLECTION_MEMBER);
            //            coll.EnsureIndex(versionKeys);

        }

        /// <summary>
        /// Does a complete wipe and reset of the database. USE WITH CAUTION!
        /// </summary>
        public void Clean()
        {
            EraseData();
            EnsureIndices();
        }

        private BundleEntry reconstituteBundleEntry(BsonDocument doc, bool fetchContent)
        {
            if (doc == null) return null;

            // Remove our storage metadata before deserializing
            doc.Remove(BSON_VERSIONDATE_MEMBER);
            //            doc.Remove(BSON_VERSIONDATE_MEMBER_ISO);
            doc.Remove(BSON_STATE_MEMBER);
            doc.Remove(BSON_RECORDID_MEMBER);
            doc.Remove(BSON_ENTRY_TYPE_MEMBER);
            doc.Remove(BSON_COLLECTION_MEMBER);
            doc.Remove(BSON_BATCHID_MEMBER);

            var json = doc.ToJson();

            BundleEntry e;
            try
            {
                e = FhirParser.ParseBundleEntryFromJson(json);
            }
            catch (Exception inner)
            {
                throw new InvalidOperationException("Cannot parse MongoDb's json into a feed entry: ", inner);
            }

            if (fetchContent == true)
            {
                // Only fetch binaries from Amazon if we're configured to do so, otherwise the
                // binary data will already be in the parsed entry
                if (e is ResourceEntry<Binary> && Config.Settings.UseS3)
                {
                    var be = (ResourceEntry<Binary>)e;

                    var blobId = calculateBlobName(be.Links.SelfLink);

                    using (var blobStorage = getBlobStorage())
                    {
                        if (blobStorage != null)
                        {
                            blobStorage.Open();
                            be.Resource.Content = blobStorage.Fetch(blobId);
                            blobStorage.Close();
                        }
                    }
                }
            }

            return e;
        }

        private MongoCollection<BsonDocument> getResourceCollection()
        {
            return database.GetCollection(RESOURCE_COLLECTION);
        }

        private MongoCollection<BsonDocument> ResourceCollection
        {
            get
            {
                return database.GetCollection(RESOURCE_COLLECTION);
            }
        }
    }
}
