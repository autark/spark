﻿/* 
 * Copyright (c) 2014, Furore (info@furore.com) and contributors
 * See the file CONTRIBUTORS for details.
 * 
 * This file is licensed under the BSD 3-Clause license
 * available at https://raw.github.com/furore-fhir/spark/master/LICENSE
 */

using System;
using Hl7.Fhir.Model;
using System.Collections.Generic;
using System.Collections.Specialized;
using Hl7.Fhir.Rest;

namespace Spark.Core
{
    public interface IFhirIndex
    {
        void Clean();
        void Process(IEnumerable<Interaction> interactions);
        void Process(Interaction interaction);
        SearchResults Search(string resource, SearchParams searchCommand);
        Key FindSingle(string resource, SearchParams searchCommand);
    }
}
