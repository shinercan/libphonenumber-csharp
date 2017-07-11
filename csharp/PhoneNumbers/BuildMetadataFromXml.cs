﻿/*
 * Copyright (C) 2009 Google Inc.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 * http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Reflection;
using System.Xml.Linq;
using System.Diagnostics;
namespace PhoneNumbers
{
    public class BuildMetadataFromXml
    {
        // String constants used to fetch the XML nodes and attributes.
        private static readonly String CARRIER_CODE_FORMATTING_RULE = "carrierCodeFormattingRule";
        private static readonly String CARRIER_SPECIFIC = "carrierSpecific";
        private static readonly String COUNTRY_CODE = "countryCode";
        private static readonly String EMERGENCY = "emergency";
        private static readonly String EXAMPLE_NUMBER = "exampleNumber";
        private static readonly String FIXED_LINE = "fixedLine";
        private static readonly String FORMAT = "format";
        private static readonly String GENERAL_DESC = "generalDesc";
        private static readonly String INTERNATIONAL_PREFIX = "internationalPrefix";
        private static readonly String INTL_FORMAT = "intlFormat";
        private static readonly String LEADING_DIGITS = "leadingDigits";
        private static readonly String LEADING_ZERO_POSSIBLE = "leadingZeroPossible";
        private static readonly String MAIN_COUNTRY_FOR_CODE = "mainCountryForCode";
        private static readonly String MOBILE = "mobile";
        private static readonly String NATIONAL_NUMBER_PATTERN = "nationalNumberPattern";
        private static readonly String NATIONAL_PREFIX = "nationalPrefix";
        private static readonly String NATIONAL_PREFIX_FORMATTING_RULE = "nationalPrefixFormattingRule";
        private static readonly String NATIONAL_PREFIX_OPTIONAL_WHEN_FORMATTING =
            "nationalPrefixOptionalWhenFormatting";
        private static readonly String NATIONAL_PREFIX_FOR_PARSING = "nationalPrefixForParsing";
        private static readonly String NATIONAL_PREFIX_TRANSFORM_RULE = "nationalPrefixTransformRule";
        private static readonly String NO_INTERNATIONAL_DIALLING = "noInternationalDialling";
        private static readonly String NUMBER_FORMAT = "numberFormat";
        private static readonly String PAGER = "pager";
        private static readonly String PATTERN = "pattern";
        private static readonly String PERSONAL_NUMBER = "personalNumber";
        private static readonly String POSSIBLE_NUMBER_PATTERN = "possibleNumberPattern";
        private static readonly String POSSIBLE_LENGTHS = "possibleLengths";
        private static readonly String NATIONAL = "national";
        private static readonly String LOCAL_ONLY = "localOnly";
        private static readonly String PREFERRED_EXTN_PREFIX = "preferredExtnPrefix";
        private static readonly String PREFERRED_INTERNATIONAL_PREFIX = "preferredInternationalPrefix";
        private static readonly String PREMIUM_RATE = "premiumRate";
        private static readonly String SHARED_COST = "sharedCost";
        private static readonly String SHORT_CODE = "shortCode";
        private static readonly String STANDARD_RATE = "standardRate";
        private static readonly String TOLL_FREE = "tollFree";
        private static readonly String UAN = "uan";
        private static readonly String VOICEMAIL = "voicemail";
        private static readonly String VOIP = "voip";

        private static readonly HashSet<string> PHONE_NUMBER_DESCS_WITHOUT_MATCHING_TYPES = new HashSet<string>{NO_INTERNATIONAL_DIALLING};

        // Build the PhoneMetadataCollection from the input XML file.
        public static PhoneMetadataCollection BuildPhoneMetadataCollection(Stream input,
            bool liteBuild, bool specialBuild)
        {
            var document = XDocument.Load(input);
            var isShortNumberMetadata = document.GetElementsByTagName("ShortNumberMetadata").Count() != 0;
            var isAlternateFormatsMetadata = document.GetElementsByTagName("PhoneNumberAlternateFormats").Count() != 0;
            return BuildPhoneMetadataCollection(document, liteBuild, specialBuild,
                isShortNumberMetadata, isAlternateFormatsMetadata);
        }

        // @VisibleForTesting
        static PhoneMetadataCollection BuildPhoneMetadataCollection(XDocument document,
            bool liteBuild, bool specialBuild, bool isShortNumberMetadata,
            bool isAlternateFormatsMetadata)
        {
            var metadataCollection = new PhoneMetadataCollection.Builder();
            var metadataFilter = GetMetadataFilter(liteBuild, specialBuild);
            foreach (XElement territory in document.GetElementsByTagName("territory"))
            {
                String regionCode = "";
                // For the main metadata file this should always be set, but for other supplementary data
                // files the country calling code may be all that is needed.
                if (territory.HasAttribute("id"))
                     regionCode = territory.GetAttribute("id");
                var metadata = LoadCountryMetadata(regionCode, territory,
                    isShortNumberMetadata, isAlternateFormatsMetadata);
                metadataFilter.FilterMetadata(metadata);
                metadataCollection.AddMetadata(metadata);
            }
            return metadataCollection.Build();
        }

        // Build a mapping from a country calling code to the region codes which denote the country/region
        // represented by that country code. In the case of multiple countries sharing a calling code,
        // such as the NANPA countries, the one indicated with "isMainCountryForCode" in the metadata
        // should be first.
        public static Dictionary<int, List<String>> BuildCountryCodeToRegionCodeMap(
            PhoneMetadataCollection metadataCollection)
        {
            Dictionary<int, List<String>> countryCodeToRegionCodeMap =
                new Dictionary<int, List<String>>();
            foreach (PhoneMetadata metadata in metadataCollection.MetadataList)
            {
                String regionCode = metadata.Id;
                int countryCode = metadata.CountryCode;
                if (countryCodeToRegionCodeMap.ContainsKey(countryCode))
                {
                    if (metadata.MainCountryForCode)
                        countryCodeToRegionCodeMap[countryCode].Insert(0, regionCode);
                    else
                        countryCodeToRegionCodeMap[countryCode].Add(regionCode);
                }
                else
                {
                    // For most countries, there will be only one region code for the country calling code.
                    List<String> listWithRegionCode = new List<String>(1);
                    if(regionCode.Length > 0)
                        listWithRegionCode.Add(regionCode);
                    countryCodeToRegionCodeMap[countryCode] = listWithRegionCode;
                }
            }
            return countryCodeToRegionCodeMap;
        }

        public static String ValidateRE(String regex)
        {
            return ValidateRE(regex, false);
        }

        public static String ValidateRE(String regex, bool removeWhitespace)
        {
            // Removes all the whitespace and newline from the regexp. Not using pattern compile options to
            // make it work across programming languages.
            if (removeWhitespace)
                regex = Regex.Replace(regex, "\\s", "");
            new Regex(regex, InternalRegexOptions.Default);
            // return regex itself if it is of correct regex syntax
            // i.e. compile did not fail with a PatternSyntaxException.
            return regex;
        }

        /**
        * Returns the national prefix of the provided country element.
        */
        // @VisibleForTesting
        public static String GetNationalPrefix(XElement element)
        {
            return element.HasAttribute(NATIONAL_PREFIX) ? element.GetAttribute(NATIONAL_PREFIX) : "";
        }

        public static PhoneMetadata.Builder LoadTerritoryTagMetadata(String regionCode, XElement element,
                                                        String nationalPrefix)
        {
            var metadata = new PhoneMetadata.Builder();
            metadata.SetId(regionCode);
            metadata.SetCountryCode(int.Parse(element.GetAttribute(COUNTRY_CODE)));
            if (element.HasAttribute(LEADING_DIGITS))
                metadata.SetLeadingDigits(ValidateRE(element.GetAttribute(LEADING_DIGITS)));
            metadata.SetInternationalPrefix(ValidateRE(element.GetAttribute(INTERNATIONAL_PREFIX)));
            if (element.HasAttribute(PREFERRED_INTERNATIONAL_PREFIX))
            {
                String preferredInternationalPrefix = element.GetAttribute(PREFERRED_INTERNATIONAL_PREFIX);
                metadata.SetPreferredInternationalPrefix(preferredInternationalPrefix);
            }
            if (element.HasAttribute(NATIONAL_PREFIX_FOR_PARSING))
            {
                metadata.SetNationalPrefixForParsing(
                    ValidateRE(element.GetAttribute(NATIONAL_PREFIX_FOR_PARSING), true));
                if (element.HasAttribute(NATIONAL_PREFIX_TRANSFORM_RULE))
                {
                    metadata.SetNationalPrefixTransformRule(
                    ValidateRE(element.GetAttribute(NATIONAL_PREFIX_TRANSFORM_RULE)));
                }
            }
            if (!String.IsNullOrEmpty(nationalPrefix))
            {
                metadata.SetNationalPrefix(nationalPrefix);
                if (!metadata.HasNationalPrefixForParsing)
                    metadata.SetNationalPrefixForParsing(nationalPrefix);
            }
            if (element.HasAttribute(PREFERRED_EXTN_PREFIX))
            {
                metadata.SetPreferredExtnPrefix(element.GetAttribute(PREFERRED_EXTN_PREFIX));
            }
            if (element.HasAttribute(MAIN_COUNTRY_FOR_CODE))
            {
                metadata.SetMainCountryForCode(true);
            }
            if (element.HasAttribute(LEADING_ZERO_POSSIBLE))
            {
                metadata.SetLeadingZeroPossible(true);
            }
            return metadata;
        }

        /**
        * Extracts the pattern for international format. If there is no intlFormat, default to using the
        * national format. If the intlFormat is set to "NA" the intlFormat should be ignored.
        *
        * @throws  RuntimeException if multiple intlFormats have been encountered.
        * @return  whether an international number format is defined.
        */
        // @VisibleForTesting
        public static bool LoadInternationalFormat(PhoneMetadata.Builder metadata,
            XElement numberFormatElement,
            String nationalFormat)
        {
            NumberFormat.Builder intlFormat = new NumberFormat.Builder();
            SetLeadingDigitsPatterns(numberFormatElement, intlFormat);
            intlFormat.SetPattern(numberFormatElement.GetAttribute(PATTERN));
            var intlFormatPattern = numberFormatElement.GetElementsByTagName(INTL_FORMAT).ToList();
            bool hasExplicitIntlFormatDefined = false;

            if (intlFormatPattern.Count > 1)
            {
                //LOGGER.log(Level.SEVERE,
                //          "A maximum of one intlFormat pattern for a numberFormat element should be " +
                //           "defined.");
                throw new Exception("Invalid number of intlFormat patterns for country: " +
                                    metadata.Id);
            }
            else if (intlFormatPattern.Count == 0)
            {
                // Default to use the same as the national pattern if none is defined.
                intlFormat.SetFormat(nationalFormat);
            }
            else
            {
                var intlFormatPatternValue = intlFormatPattern.First().Value;
                if (!intlFormatPatternValue.Equals("NA"))
                {
                    intlFormat.SetFormat(intlFormatPatternValue);
                }
                hasExplicitIntlFormatDefined = true;
            }

            if (intlFormat.HasFormat)
            {
                metadata.AddIntlNumberFormat(intlFormat);
            }
            return hasExplicitIntlFormatDefined;
        }

        /**
         * Extracts the pattern for the national format.
         *
         * @throws  RuntimeException if multiple or no formats have been encountered.
         * @return  the national format string.
         */
        // @VisibleForTesting
        public static String LoadNationalFormat(PhoneMetadata.Builder metadata, XElement numberFormatElement,
                                         NumberFormat.Builder format)
        {
            SetLeadingDigitsPatterns(numberFormatElement, format);
            format.SetPattern(ValidateRE(numberFormatElement.GetAttribute(PATTERN)));

            var formatPattern = numberFormatElement.GetElementsByTagName(FORMAT).ToList();
            if (formatPattern.Count != 1)
            {
                //LOGGER.log(Level.SEVERE,
                //           "Only one format pattern for a numberFormat element should be defined.");
                throw new Exception("Invalid number of format patterns for country: " +
                                    metadata.Id);
            }
            var nationalFormat = formatPattern[0].Value;
            format.SetFormat(nationalFormat);
            return nationalFormat;
        }

        /**
        *  Extracts the available formats from the provided DOM element. If it does not contain any
        *  nationalPrefixFormattingRule, the one passed-in is retained. The nationalPrefix,
        *  nationalPrefixFormattingRule and nationalPrefixOptionalWhenFormatting values are provided from
        *  the parent (territory) element.
        */
        // @VisibleForTesting
        public static void LoadAvailableFormats(PhoneMetadata.Builder metadata,
                                         XElement element, String nationalPrefix,
                                         String nationalPrefixFormattingRule,
                                         bool nationalPrefixOptionalWhenFormatting)
        {
            String carrierCodeFormattingRule = "";
            if (element.HasAttribute(CARRIER_CODE_FORMATTING_RULE))
            {
                carrierCodeFormattingRule = ValidateRE(
                    GetDomesticCarrierCodeFormattingRuleFromElement(element, nationalPrefix));
            }
            var numberFormatElements = element.GetElementsByTagName(NUMBER_FORMAT);
            bool hasExplicitIntlFormatDefined = false;

            int numOfFormatElements = numberFormatElements.Count();
            if (numOfFormatElements > 0)
            {
                foreach (var numberFormatElement in numberFormatElements)
                {
                    var format = new NumberFormat.Builder();

                    if (numberFormatElement.HasAttribute(NATIONAL_PREFIX_FORMATTING_RULE))
                    {
                        format.SetNationalPrefixFormattingRule(
                            GetNationalPrefixFormattingRuleFromElement(numberFormatElement, nationalPrefix));
                        format.SetNationalPrefixOptionalWhenFormatting(
                            numberFormatElement.HasAttribute(NATIONAL_PREFIX_OPTIONAL_WHEN_FORMATTING));

                    }
                    else
                    {
                        format.SetNationalPrefixFormattingRule(nationalPrefixFormattingRule);
                        format.SetNationalPrefixOptionalWhenFormatting(nationalPrefixOptionalWhenFormatting);
                    }
                    if (numberFormatElement.HasAttribute("carrierCodeFormattingRule"))
                    {
                        format.SetDomesticCarrierCodeFormattingRule(ValidateRE(
                            GetDomesticCarrierCodeFormattingRuleFromElement(
                                numberFormatElement, nationalPrefix)));
                    }
                    else
                    {
                        format.SetDomesticCarrierCodeFormattingRule(carrierCodeFormattingRule);
                    }

                    // Extract the pattern for the national format.
                    String nationalFormat =
                        LoadNationalFormat(metadata, numberFormatElement, format);
                    metadata.AddNumberFormat(format);

                    if (LoadInternationalFormat(metadata, numberFormatElement, nationalFormat))
                    {
                        hasExplicitIntlFormatDefined = true;
                    }
                }
                // Only a small number of regions need to specify the intlFormats in the xml. For the majority
                // of countries the intlNumberFormat metadata is an exact copy of the national NumberFormat
                // metadata. To minimize the size of the metadata file, we only keep intlNumberFormats that
                // actually differ in some way to the national formats.
                if (!hasExplicitIntlFormatDefined)
                {
                    metadata.ClearIntlNumberFormat();
                }
            }
        }

        public static void SetLeadingDigitsPatterns(XElement numberFormatElement, NumberFormat.Builder format)
        {
            foreach (var e in numberFormatElement.GetElementsByTagName(LEADING_DIGITS))
            {
                format.AddLeadingDigitsPattern(ValidateRE(e.Value, true));
            }
        }

        public static String GetNationalPrefixFormattingRuleFromElement(XElement element,
            String nationalPrefix)
        {
            String nationalPrefixFormattingRule = element.GetAttribute(NATIONAL_PREFIX_FORMATTING_RULE);
            // Replace $NP with national prefix and $FG with the first group ($1).
            nationalPrefixFormattingRule = ReplaceFirst(nationalPrefixFormattingRule, "$NP", nationalPrefix);
            nationalPrefixFormattingRule = ReplaceFirst(nationalPrefixFormattingRule, "$FG", "${1}");
            return nationalPrefixFormattingRule;
        }

        public static String GetDomesticCarrierCodeFormattingRuleFromElement(XElement element,
            String nationalPrefix)
        {
            String carrierCodeFormattingRule = element.GetAttribute(CARRIER_CODE_FORMATTING_RULE);
            // Replace $FG with the first group ($1) and $NP with the national prefix.
            carrierCodeFormattingRule = ReplaceFirst(carrierCodeFormattingRule, "$FG", "${1}");
            carrierCodeFormattingRule = ReplaceFirst(carrierCodeFormattingRule, "$NP", nationalPrefix);
            return carrierCodeFormattingRule;
        }
        
        /**
        * Checks if the possible lengths provided as a sorted set are equal to the possible lengths
        * stored already in the description pattern. Note that possibleLengths may be empty but must not
        * be null, and the PhoneNumberDesc passed in should also not be null.
        */
        private static bool ArePossibleLengthsEqual(SortedSet<int> possibleLengths,
            PhoneNumberDesc desc)
        {
            if (possibleLengths.Count != desc.PossibleLengthCount)
            {
                return false;
            }
            // Note that both should be sorted already, and we know they are the same length.
            int i = 0;
            foreach (int length in possibleLengths)
            {
                if (length != desc.PossibleLengthList[i])
                {
                    return false;
                }
                i++;
            }
            return true;
        }

        /**
        * Processes a phone number description element from the XML file and returns it as a
        * PhoneNumberDesc. If the description element is a fixed line or mobile number, the parent
        * description will be used to fill in the whole element if necessary, or any components that are
        * missing. For all other types, the parent description will only be used to fill in missing
        * components if the type has a partial definition. For example, if no "tollFree" element exists,
        * we assume there are no toll free numbers for that locale, and return a phone number description
        * with "NA" for both the national and possible number patterns.
        *
        * @param generalDesc  a generic phone number description that will be used to fill in missing
        *                     parts of the description
        * @param countryElement  the XML element representing all the country information
        * @param numberType  the name of the number type, corresponding to the appropriate tag in the XML
        *                    file with information about that type
        * @return  complete description of that phone number type
        */
        public static PhoneNumberDesc.Builder ProcessPhoneNumberDescElement(PhoneNumberDesc parentDesc,
            XElement countryElement, String numberType)
        {
            if (parentDesc == null)
                parentDesc = new PhoneNumberDesc.Builder().Build();
            var phoneNumberDescList = countryElement.GetElementsByTagName(numberType).ToList();
            var numberDesc = new PhoneNumberDesc.Builder();
            if (phoneNumberDescList.Count == 0)
            {
                // -1 will never match a possible phone number length, so is safe to use to ensure this never
                // matches. We don't leave it empty, since for compression reasons, we use the empty list to
                // mean that the generalDesc possible lengths apply.
                numberDesc.AddPossibleLength(-1);
                return numberDesc;
            }
            if (phoneNumberDescList.Count > 0)
            {
                if (phoneNumberDescList.Count > 1)
                {
                    throw new Exception($"Multiple elements with type {numberType} found.");
                }
                var element = phoneNumberDescList[0];

                if (parentDesc != null)
                {
                    // New way of handling possible number lengths. We don't do this for the general
                    // description, since these tags won't be present; instead we will calculate its values
                    // based on the values for all the other number type descriptions (see
                    // setPossibleLengthsGeneralDesc).
                    var lengths = new SortedSet<int>();
                    var localOnlyLengths = new SortedSet<int>();
                    PopulatePossibleLengthSets(element, lengths, localOnlyLengths);
                    SetPossibleLengths(lengths, new SortedSet<int>(), parentDesc, numberDesc);
                }

                var validPattern = element.GetElementsByTagName(NATIONAL_NUMBER_PATTERN).ToList();
                if (validPattern.Any())
                    numberDesc.SetNationalNumberPattern(ValidateRE(validPattern.First().Value, true));

                var exampleNumber = element.GetElementsByTagName(EXAMPLE_NUMBER).ToList();
                if (exampleNumber.Any())
                    numberDesc.SetExampleNumber(exampleNumber.First().Value);

            }
            return numberDesc;
        }

        // @VisibleForTesting
        static void SetRelevantDescPatterns(PhoneMetadata.Builder metadata, XElement element,
            bool isShortNumberMetadata)
        {
            PhoneNumberDesc.Builder generalDescBuilder = ProcessPhoneNumberDescElement(null, element,
                GENERAL_DESC);
            // Calculate the possible lengths for the general description. This will be based on the
            // possible lengths of the child elements.
            SetPossibleLengthsGeneralDesc(
                generalDescBuilder, metadata.Id, element, isShortNumberMetadata);
            metadata.SetGeneralDesc(generalDescBuilder);

            PhoneNumberDesc generalDesc = metadata.GeneralDesc;

            if (!isShortNumberMetadata)
            {
                // Set fields used by regular length phone numbers.
                metadata.SetFixedLine(ProcessPhoneNumberDescElement(generalDesc, element, FIXED_LINE));
                metadata.SetMobile(ProcessPhoneNumberDescElement(generalDesc, element, MOBILE));
                metadata.SetSharedCost(ProcessPhoneNumberDescElement(generalDesc, element, SHARED_COST));
                metadata.SetVoip(ProcessPhoneNumberDescElement(generalDesc, element, VOIP));
                metadata.SetPersonalNumber(ProcessPhoneNumberDescElement(generalDesc, element,
                    PERSONAL_NUMBER));
                metadata.SetPager(ProcessPhoneNumberDescElement(generalDesc, element, PAGER));
                metadata.SetUan(ProcessPhoneNumberDescElement(generalDesc, element, UAN));
                metadata.SetVoicemail(ProcessPhoneNumberDescElement(generalDesc, element, VOICEMAIL));
                metadata.SetNoInternationalDialling(ProcessPhoneNumberDescElement(generalDesc, element,
                    NO_INTERNATIONAL_DIALLING));
                bool mobileAndFixedAreSame = metadata.Mobile.NationalNumberPattern
                    .Equals(metadata.FixedLine.NationalNumberPattern);
                if (metadata.SameMobileAndFixedLinePattern != mobileAndFixedAreSame)
                {
                    // Set this if it is not the same as the default.
                    metadata.SetSameMobileAndFixedLinePattern(mobileAndFixedAreSame);
                }
                metadata.SetTollFree(ProcessPhoneNumberDescElement(generalDesc, element, TOLL_FREE));
                metadata.SetPremiumRate(ProcessPhoneNumberDescElement(generalDesc, element, PREMIUM_RATE));
            }
            else
            {
                // Set fields used by short numbers.
                metadata.SetStandardRate(ProcessPhoneNumberDescElement(generalDesc, element, STANDARD_RATE));
                metadata.SetShortCode(ProcessPhoneNumberDescElement(generalDesc, element, SHORT_CODE));
                metadata.SetCarrierSpecific(ProcessPhoneNumberDescElement(generalDesc, element,
                    CARRIER_SPECIFIC));
                metadata.SetEmergency(ProcessPhoneNumberDescElement(generalDesc, element, EMERGENCY));
                metadata.SetTollFree(ProcessPhoneNumberDescElement(generalDesc, element, TOLL_FREE));
                metadata.SetPremiumRate(ProcessPhoneNumberDescElement(generalDesc, element, PREMIUM_RATE));
            }
        }

        private static ISet<int> ParsePossibleLengthStringToSet(String possibleLengthString)
        {
            if (possibleLengthString.Length == 0)
            {
                throw new Exception("Empty possibleLength string found.");
            }
            String[] lengths = possibleLengthString.Split(',');
            ISet<int> lengthSet = new SortedSet<int>();
            for (int i = 0; i < lengths.Length; i++)
            {
                String lengthSubstring = lengths[i];
                if (lengthSubstring.Length == 0)
                {
                    throw new Exception("Leading, trailing or adjacent commas in possible " +
                                        $"length string {possibleLengthString}, these should only separate numbers or ranges.");
                }
                else if (lengthSubstring[0] == '[')
                {
                    if (lengthSubstring[lengthSubstring.Length - 1] != ']')
                    {
                        throw new Exception("Missing end of range character in possible " +
                                            $"length string {possibleLengthString}.");
                    }
                    // Strip the leading and trailing [], and split on the -.
                    String[] minMax = lengthSubstring.Substring(1, lengthSubstring.Length - 2).Split('-');
                    if (minMax.Length != 2)
                    {
                        throw new Exception("Ranges must have exactly one - character: " +
                                            $"missing for {possibleLengthString}.");
                    }
                    int min = int.Parse(minMax[0]);
                    int max = int.Parse(minMax[1]);
                    // We don't even accept [6-7] since we prefer the shorter 6,7 variant; for a range to be in
                    // use the hyphen needs to replace at least one digit.
                    if (max - min < 2)
                    {
                        throw new Exception("The first number in a range should be two or " +
                                            $"more digits lower than the second. Culprit possibleLength string: {possibleLengthString}");
                    }
                    for (int j = min; j <= max; j++)
                    {
                        if (!lengthSet.Add(j))
                        {
                            throw new Exception($"Duplicate length element found ({j}) in " +
                                                $"possibleLength string {possibleLengthString}");
                        }
                    }
                }
                else
                {
                    int length = int.Parse(lengthSubstring);
                    if (!lengthSet.Add(length))
                    {
                        throw new Exception($"Duplicate length element found ({length}) in " +
                                            $"possibleLength string {possibleLengthString}");
                    }
                }
            }
            return lengthSet;
        }

        /**
         * Reads the possible lengths present in the metadata and splits them into two sets: one for
         * full-length numbers, one for local numbers.
         *
         * @param data  one or more phone number descriptions, represented as XML nodes
         * @param lengths  a set to which to add possible lengths of full phone numbers
         * @param localOnlyLengths  a set to which to add possible lengths of phone numbers only diallable
         *     locally (e.g. within a province)
         */
        private static void PopulatePossibleLengthSets(XElement data, SortedSet<int> lengths,
             SortedSet<int> localOnlyLengths)
        {
            var possibleLengths = data.GetElementsByTagName(POSSIBLE_LENGTHS).ToArray();
            for (int i = 0; i < possibleLengths.Count(); i++)
            {
                XElement element = (XElement)possibleLengths[i];
                String nationalLengths = element.GetAttribute(NATIONAL);
                // We don't add to the phone metadata yet, since we want to sort length elements found under
                // different nodes first, make sure there are no duplicates between them and that the
                // localOnly lengths don't overlap with the others.
                ISet<int> thisElementLengths = ParsePossibleLengthStringToSet(nationalLengths);
                if (element.HasAttribute(LOCAL_ONLY))
                {
                    String localLengths = element.GetAttribute(LOCAL_ONLY);
                    ISet<int> thisElementLocalOnlyLengths = ParsePossibleLengthStringToSet(localLengths);
                    var intersection = thisElementLengths.Intersect(thisElementLocalOnlyLengths).ToList();
                    if (intersection.Count != 0)
                    {
                        throw new Exception(
                            $"Possible length(s) found specified as a normal and local-only length: {intersection}");
                    }
                    // We check again when we set these lengths on the metadata itself in setPossibleLengths
                    // that the elements in localOnly are not also in lengths. For e.g. the generalDesc, it
                    // might have a local-only length for one type that is a normal length for another type. We
                    // don't consider this an error, but we do want to remove the local-only lengths.
                    foreach (var length in thisElementLocalOnlyLengths)
                    {
                        localOnlyLengths.Add(length);
                    }
                }
                // It is okay if at this time we have duplicates, because the same length might be possible
                // for e.g. fixed-line and for mobile numbers, and this method operates potentially on
                // multiple phoneNumberDesc XML elements.
                foreach (var length in thisElementLengths)
                {
                    lengths.Add(length);
                }
            }
        }

        /**
         * Sets possible lengths in the general description, derived from certain child elements.
         */
        // @VisibleForTesting
        static void SetPossibleLengthsGeneralDesc(PhoneNumberDesc.Builder generalDesc, String metadataId,
            XElement data, bool isShortNumberMetadata)
        {
            SortedSet<int> lengths = new SortedSet<int>();
            SortedSet<int> localOnlyLengths = new SortedSet<int>();
            // The general description node should *always* be present if metadata for other types is
            // present, aside from in some unit tests.
            // (However, for e.g. formatting metadata in PhoneNumberAlternateFormats, no PhoneNumberDesc
            // elements are present).
            var generalDescNodes = data.GetElementsByTagName(GENERAL_DESC);
            if (generalDescNodes.Any())
            {
                XElement generalDescNode = (XElement)generalDescNodes.ElementAt(0);
                PopulatePossibleLengthSets(generalDescNode, lengths, localOnlyLengths);
                if (lengths.Count != 0 || localOnlyLengths.Count != 0)
                {
                    // We shouldn't have anything specified at the "general desc" level: we are going to
                    // calculate this ourselves from child elements.
                    throw new Exception("Found possible lengths specified at general " +
                                        $"desc: this should be derived from child elements. Affected country: {metadataId}");
                }
            }
            if (!isShortNumberMetadata)
            {
                // Make a copy here since we want to remove some nodes, but we don't want to do that on our
                // actual data.
                XElement allDescData = new XElement(data);
                foreach (String tag in PHONE_NUMBER_DESCS_WITHOUT_MATCHING_TYPES)
                {
                    var nodesToRemove = allDescData.GetElementsByTagName(tag);
                    if (nodesToRemove.Any())
                    {
                        // We check when we process phone number descriptions that there are only one of each
                        // type, so this is safe to do.
                        nodesToRemove.ElementAt(0).Remove();
                    }
                }
                PopulatePossibleLengthSets(allDescData, lengths, localOnlyLengths);
            }
            else
            {
                // For short number metadata, we want to copy the lengths from the "short code" section only.
                // This is because it's the more detailed validation pattern, it's not a sub-type of short
                // codes. The other lengths will be checked later to see that they are a sub-set of these
                // possible lengths.
                var shortCodeDescList = data.GetElementsByTagName(SHORT_CODE);
                if (shortCodeDescList.Any())
                {
                    XElement shortCodeDesc = (XElement)shortCodeDescList.ElementAt(0);
                    PopulatePossibleLengthSets(shortCodeDesc, lengths, localOnlyLengths);
                }
                if (localOnlyLengths.Count > 0)
                {
                    throw new Exception("Found local-only lengths in short-number metadata");
                }
            }
            SetPossibleLengths(lengths, localOnlyLengths, null, generalDesc);
        }
        
        /**
        * Sets the possible length fields in the metadata from the sets of data passed in. Checks that
        * the length is covered by the "parent" phone number description element if one is present, and
        * if the lengths are exactly the same as this, they are not filled in for efficiency reasons.
        *
        * @param parentDesc  the "general description" element or null if desc is the generalDesc itself
        * @param desc  the PhoneNumberDesc object that we are going to set lengths for
        */
        private static void SetPossibleLengths(SortedSet<int> lengths,
            SortedSet<int> localOnlyLengths, PhoneNumberDesc parentDesc, PhoneNumberDesc.Builder desc)
        {
            // Only add the lengths to this sub-type if they aren't exactly the same as the possible
            // lengths in the general desc (for metadata size reasons).
            if (parentDesc == null || !ArePossibleLengthsEqual(lengths, parentDesc))
            {
                foreach (int length in lengths)
                {
                    if (parentDesc == null || parentDesc.PossibleLengthList.Contains(length))
                    {
                        desc.PossibleLengthList.Add(length);
                    }
                    else
                    {
                        // We shouldn't have possible lengths defined in a child element that are not covered by
                        // the general description. We check this here even though the general description is
                        // derived from child elements because it is only derived from a subset, and we need to
                        // ensure *all* child elements have a valid possible length.
                        throw new Exception(
                            $"Out-of-range possible length found ({length}), parent lengths {string.Join(", ", parentDesc.PossibleLengthList)}.");
                    }
                }
            }
            // We check that the local-only length isn't also a normal possible length (only relevant for
            // the general-desc, since within elements such as fixed-line we would throw an exception if we
            // saw this) before adding it to the collection of possible local-only lengths.
            foreach (int length in localOnlyLengths)
            {
                if (!lengths.Contains(length))
                {
                    // We check it is covered by either of the possible length sets of the parent
                    // PhoneNumberDesc, because for example 7 might be a valid localOnly length for mobile, but
                    // a valid national length for fixedLine, so the generalDesc would have the 7 removed from
                    // localOnly.
                    if (parentDesc == null || parentDesc.PossibleLengthLocalOnlyList.Contains(length)
                        || parentDesc.PossibleLengthList.Contains(length))
                    {
                        desc.PossibleLengthLocalOnlyList.Add(length);
                    }
                    else
                    {
                        throw new Exception(
                            $"Out-of-range local-only possible length found ({length}), parent length {string.Join(", ", parentDesc.PossibleLengthLocalOnlyList)}.");
                    }
                }
            }
        }


        private static String ReplaceFirst(String input, String value, String replacement)
        {
            var p = input.IndexOf(value);
            if (p >= 0)
                input = input.Substring(0, p) + replacement + input.Substring(p + value.Length);
            return input;
        }

        // @VisibleForTesting
        public static void LoadGeneralDesc(PhoneMetadata.Builder metadata, XElement element)
        {
            var generalDescBuilder = ProcessPhoneNumberDescElement(null, element, GENERAL_DESC);
            SetPossibleLengthsGeneralDesc(generalDescBuilder, metadata.Id, element, false);
            var generalDesc = generalDescBuilder.Build();

            metadata.SetFixedLine(ProcessPhoneNumberDescElement(generalDesc, element, FIXED_LINE));
            metadata.SetMobile(ProcessPhoneNumberDescElement(generalDesc, element, MOBILE));
            metadata.SetTollFree(ProcessPhoneNumberDescElement(generalDesc, element, TOLL_FREE));
            metadata.SetPremiumRate(ProcessPhoneNumberDescElement(generalDesc, element, PREMIUM_RATE));
            metadata.SetSharedCost(ProcessPhoneNumberDescElement(generalDesc, element, SHARED_COST));
            metadata.SetVoip(ProcessPhoneNumberDescElement(generalDesc, element, VOIP));
            metadata.SetPersonalNumber(ProcessPhoneNumberDescElement(generalDesc, element, PERSONAL_NUMBER));
            metadata.SetPager(ProcessPhoneNumberDescElement(generalDesc, element, PAGER));
            metadata.SetUan(ProcessPhoneNumberDescElement(generalDesc, element, UAN));
            metadata.SetVoicemail(ProcessPhoneNumberDescElement(generalDesc, element, VOICEMAIL));
            metadata.SetEmergency(ProcessPhoneNumberDescElement(generalDesc, element, EMERGENCY));
            metadata.SetNoInternationalDialling(ProcessPhoneNumberDescElement(generalDesc, element, NO_INTERNATIONAL_DIALLING));
            metadata.SetSameMobileAndFixedLinePattern(
                metadata.Mobile.NationalNumberPattern.Equals(
                metadata.FixedLine.NationalNumberPattern));
        }

        public static PhoneMetadata.Builder LoadCountryMetadata(String regionCode,
            XElement element,
            bool isShortNumberMetadata,
            bool isAlternateFormatsMetadata)
        {
            String nationalPrefix = GetNationalPrefix(element);
            PhoneMetadata.Builder metadata =
                LoadTerritoryTagMetadata(regionCode, element, nationalPrefix);
            String nationalPrefixFormattingRule =
                GetNationalPrefixFormattingRuleFromElement(element, nationalPrefix);
            LoadAvailableFormats(metadata, element, nationalPrefix,
                                 nationalPrefixFormattingRule,
                                 element.HasAttribute(NATIONAL_PREFIX_OPTIONAL_WHEN_FORMATTING));
            LoadGeneralDesc(metadata, element);
            if (!isAlternateFormatsMetadata)
            {
                // The alternate formats metadata does not need most of the patterns to be set.
                SetRelevantDescPatterns(metadata, element, isShortNumberMetadata);
            }
            return metadata;
        }

        public static Dictionary<int, List<String>> GetCountryCodeToRegionCodeMap(String filePrefix)
        {
            var asm = typeof(BuildMetadataFromXml).GetTypeInfo().Assembly;
            var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(filePrefix)) ?? "missing";
            using (var stream = asm.GetManifestResourceStream(name))
            {
                var collection = BuildPhoneMetadataCollection(stream, false, false); // todo lite/special build
                return BuildCountryCodeToRegionCodeMap(collection);
            }
        }

        /**
         * Processes the custom build flags and gets a {@code MetadataFilter} which may be used to
        * filter {@code PhoneMetadata} objects. Incompatible flag combinations throw RuntimeException.
        *
        * @param liteBuild  The liteBuild flag value as given by the command-line
        * @param specialBuild  The specialBuild flag value as given by the command-line
        */
        // @VisibleForTesting
        internal static MetadataFilter GetMetadataFilter(bool liteBuild, bool specialBuild)
        {
            if (specialBuild)
            {
                if (liteBuild)
                {
                    throw new Exception("liteBuild and specialBuild may not both be set");
                }
                return MetadataFilter.ForSpecialBuild();
            }
            if (liteBuild)
            {
                return MetadataFilter.ForLiteBuild();
            }
            return MetadataFilter.EmptyFilter();
        }
    }
}