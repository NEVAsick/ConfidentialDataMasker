using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace ConfidentialDataMasker.Services;

public sealed partial class ConfidentialDataDetector
{
    private const string NameLabel = "ФИО?";

    private static readonly Dictionary<string, string> StandaloneFieldLabels =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ["фамилия"] = NameLabel,
        ["имя"] = NameLabel,
        ["отчество"] = NameLabel,
        ["фио"] = NameLabel,
        ["снилс"] = "СНИЛС?",
        ["паспорт"] = "Номер паспорта?",
        ["паспорт: серия, номер"] = "Номер паспорта?",
        ["паспорт: серия и номер"] = "Номер паспорта?",
        ["паспорт серия, номер"] = "Номер паспорта?",
        ["паспорт серия и номер"] = "Номер паспорта?",
        ["серия и номер паспорта"] = "Номер паспорта?",
        ["серия, номер паспорта"] = "Номер паспорта?",
        ["табельный номер"] = "Табельный номер?",
        ["таб. №"] = "Табельный номер?",
        ["кем выдан"] = "Паспорт: кем выдан?",
        ["дата выдачи"] = "Паспорт: дата выдачи?",
        ["код подразделения"] = "Паспорт: код подразделения?",
        ["телефон"] = "Телефон?",
        ["тел"] = "Телефон?",
        ["email"] = "Email?",
        ["электронная почта"] = "Email?",
        ["адрес электронной почты"] = "Email?",
        ["e-mail"] = "Email?",
        ["инн"] = "ИНН?",
        ["огрн"] = "ОГРН?",
        ["дата рождения"] = "Дата рождения?",
    };

    private static readonly Regex PassportPattern =
        new(@"\b\d{2}\s?\d{2}\s?(?:(?:номер|№)\s?)?\d{6}\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PassportLabeledPattern =
        new(@"\bпаспорт\w*\s*[:\-–—]?\s*(?:серия\s*)?(?:[,и]\s*)?(?:номер\s*)?[:\-–—]?\s*(?<secret>(?=(?:\D{0,3}\d){9})\d[\d\s'’`.,)№\-]{7,18}\d)(?!\d)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SnilsPattern =
        new(@"\b\d{3}[-\s]?\d{3}[-\s]?\d{3}[\s]?\d{2}\b", RegexOptions.Compiled);

    private static readonly Regex LabeledPhonePattern =
        new(@"\b(?:тел(?:ефон)?|моб(?:ильный)?|факс|tel|phone|fax|доб(?:авочный)?|вн(?:утр(?:енний|еннему|еннего)?)?)\.?(?:\s+номер\w*)?(?:\s*/\s*факс\.?)?\s*(?:\(?(?:раб|моб|дом)\.?\)?\s*)?[:\-–—]?\s*(?<secret>\+?\(?\d[\d\s()\-]{2,}\d)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CityPhonePattern =
        new(@"(?<![\d)])\(\d{3,5}\)\s?\d{1,3}[\s\-]?\d{2}[\s\-]?\d{2}(?!\d)", RegexOptions.Compiled);

    private static readonly Regex PhonePattern =
        new(@"(?<!\d)(\+7|8)[\s\-]?\(?\d{3}\)?[\s\-]?\d{3}[\s\-]?\d{2}[\s\-]?\d{2}\b", RegexOptions.Compiled);

    private static readonly Regex InnLabeledPattern =
        new(@"\bИНН\s*[:\-–—]?\s*(?<secret>\d{12}|\d{10}|(?:\d ?){11}\d|(?:\d ?){9}\d)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex InnPattern =
        new(@"\b\d{10}\b|\b\d{12}\b", RegexOptions.Compiled);

    private static readonly Regex OgrnPattern =
        new(@"\bогрн(?:ип)?\s*[:\-–—]?\s*(?<secret>\d{13,15})\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex EmailPattern =
        new(@"\b[A-Za-z0-9][A-Za-z0-9._%+\-]*@[A-Za-z0-9][A-Za-z0-9.\-]*\.[A-Za-z]{2,}\b", RegexOptions.Compiled);

    private static readonly Regex UrlPattern =
        new(@"(?<![@\w.\-])(?:https?://)?(?:www\.)?(?:(?:[a-z0-9\-]+\.)+(?:ru|com|org|net|su|info|biz|io|рф)|(?:[а-яё0-9\-]+\.)+рф)(?![\w\-])(?:/[^\s,;)»""]*)?",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex OrgRequisitesPattern =
        new(@"\b(?:ОКАТО|ОКТМО|ОКПО|ОКОПФ|ОКФС|ОКВЭД|КПП|БИК|К[ПИНШЛ]{2}(?=\s*[:\-–—]?\s*\d{9}\b))\s*[:\-–—]?\s*(?<secret>\d[\d.\s]{4,18}\d)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex BankAccountPattern =
        new(@"(?<!\d)\d{5}[\s.]?\d{3}[\s.]?\d[\s.]?\d{4}[\s.]?\d{7}(?!\d)", RegexOptions.Compiled);

    private static readonly Regex PasswordPattern =
        new(@"(?:пароль|парол[ья]|password|pwd)\s*[:\-–—]\s*(?<secret>\S{4,})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex LoginPattern =
        new(@"(?:логин|login|имя\s+пользователя|username|уч(?:[её]тная)?\s*запись)\s*[:\-–—]\s*(?<secret>(?![А-ЯЁа-яёA-Za-z]+:)\S{3,})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private const string QuotedName =
        @"(?:«[^«»\r\n]{1,80}(?:«[^«»\r\n]{1,80}»)?»|«[^«»\r\n]{1,80}«[^«»\r\n]{1,80}»|«[^«»\s]+(?:\s+[^«»\s]+){0,2}|[""'][^""'\r\n]{2,80}(?:[""'][^""'\r\n]{1,40}[""'](?=[""')]|\s*$)|[""']))";

    private const string OrgAdjective = @"(?:[А-ЯЁа-яё\-]+(?:ое|ая|ый|ий|ой|ее|ые|ие|ого|ему|ом)\s+)";

    private static readonly Regex CompanyQuotedPattern =
        new(@"(?<org>\b(?:ООО|ОАО|ЗАО|ПАО|АО|НКО|АНО|ФГУП|ГУП|МУП|ФГБУ|ФГБОУ|ГБУЗ|КГБУЗ|КГБУ|ГБУ|МБУ|МАУ|ГАУ)\s*,?\s*" +
            QuotedName + ")", RegexOptions.Compiled);

    private static readonly Regex OrgDescriptorQuotedPattern =
        new(@"(?<org>" + OrgAdjective + "{0,4}" +
            @"(?:учреждени|обществ|предприяти|организаци|компани|филиал|фонд|институт|университет|колледж|поликлиник|больниц)[а-яё]*" +
            @"(?:\s+[А-ЯЁа-яё\-]+){0,3}\s*" + QuotedName + ")",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex OrgDescriptorPattern =
        new(@"(?<org>" + OrgAdjective + "{2,4}" +
            @"(?:учреждени|предприяти|организаци)[а-яё]*(?:\s+здравоохранени[а-яё]*|\s+культуры|\s+образовани[а-яё]*)?)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private const string OrgForms = @"(?:ООО|ОАО|ЗАО|ПАО|АО)";

    private static readonly Regex CompanyUnquotedPattern =
        new(@"(?<org>\b" + OrgForms + @"\s+[А-ЯЁA-Z][А-Яа-яЁёA-Za-z\-]{2,}(?:\s+[А-ЯЁA-Z][А-Яа-яЁёA-Za-z\-]{2,})?)",
            RegexOptions.Compiled);

    private static readonly Regex CompanyFormAfterPattern =
        new(@"(?<org>[А-ЯЁA-Z][А-Яа-яЁёA-Za-z\-]+(?:\s+[А-ЯЁA-Z][А-Яа-яЁёA-Za-z\-]+){0,2}\s*\(" + OrgForms + @"\))",
            RegexOptions.Compiled);

    private const string HouseTail =
        @"(?:д\.|дом)\s*\d+[а-яА-Я]?(?:\s*[,/]?\s*(?:корп\.?|корпус|стр\.?|строение|кв\.?|квартира|оф\.?|офис|пом\.?|помещение)\s*\d+[а-яА-Я]?)*";

    private const string StreetAhead =
        @"(?=[^\r\n]{0,160}?(?:ул\.|улица|пр-т|просп|пер\.|переулок|ш\.|шоссе|б-р|бульвар|пл\.|площадь|наб\.|набережная|мкр|микрорайон|проезд|тракт|кв\.))";

    private static readonly Regex AddressPattern =
        new(@"(?<secret>\b\d{6},\s*" + StreetAhead + @"(?:(?:(?!\d{6},)[^,\r\n]){2,60},\s*){1,6}?" + HouseTail + ")",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CityAddressPattern =
        new(@"(?<secret>(?<![\w.])(?:г\.|город|пгт\.?|пос\.|с\.)\s*" + StreetAhead + @"[А-ЯЁ][^,\r\n]{1,40},\s*(?:(?:(?!\d{6},)[^,\r\n]){2,60},\s*){0,4}?" + HouseTail + ")",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PassportIssuedPattern =
        new(@"(?:паспорт|серия)[^\r\n]{0,80}?\bвыдан\w*\s*[:\-–—]?\s*(?!\s*(?:дата\s+выдачи|код\s+подразделения))(?<secret>[^\r\n]{3,150}?\d{1,2}\.\d{1,2}\.\d{2,4}(?:\s*г(?:ода|\.)?)?)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PassportIssuerBeforeCodePattern =
        new(@"\bвыдан\w*\s*[:\-–—]?\s*(?!\s*(?:дата\s+выдачи|код\s+подразделения))(?<secret>[^\r\n]{3,150}?)(?=[,;]?\s*код\s+подразделения)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PassportIssuerFieldPattern =
        new(@"\bкем\s+выдан\w*\s*[:\-–—]?\s*(?!\s*(?:дата\s+выдачи|код\s+подразделения))(?<secret>[^\r\n]{3,150}?)(?=[,;]?\s*(?:дата\s+выдачи|код\s+подразделения)|$)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PassportIssueDatePattern =
        new(@"\bдата\s+выдачи\s*[:\-–—]?\s*(?<secret>\d{1,2}[.\/]\d{1,2}[.\/]\d{2,4})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PassportDivisionCodePattern =
        new(@"\bподразделения\s*[:\-–—]?\s*(?<secret>\d{3}\s?[-–]\s?\d{3})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TabNumberPattern =
        new(@"\b(?:табельн\w*\s+(?:номер\w*|№)|таб\.\s*№)\s*[:\-–—]?\s*(?<secret>\d{3,10})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex LooseEmailPattern =
        new(@"(?<!\S)[^\s@]{2,}@[^\s@]{2,}", RegexOptions.Compiled);

    private static readonly Regex StandaloneNameLabelPattern =
        new(@"^\s*(?:фамилия|имя|отчество|фио)(?:\s*[:\-–—]\s*|\s+)(?!(?:дата|телефон|табельн|должност|подпис|паспорт|адрес|место|пол|год)\w*\b)(?<secret>\S.*)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ExecutorSurnamePattern =
        new(@"^\s*[Ии]сп\.\s*:?\s*(?<secret>[А-ЯЁ][а-яё]{2,}(?:-[А-ЯЁ][а-яё]+)?)(?![а-яё\-])(?!\s*[А-ЯЁA-Z])", RegexOptions.Compiled);

    private static readonly Regex BirthDatePattern =
        new(@"дата\s+рожд(?:ения|\.)\s*[:\-–—]?\s*(?<secret>\d{1,2}[.\/]\d{1,2}[.\/]\d{2,4})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CompanySuffixPattern =
        new(@"\b(?<org>[A-ZА-ЯЁ][\wа-яёA-Za-z&\-\.]*(?:\s+[A-ZА-ЯЁ][\wа-яёA-Za-z&\-\.]*){0,3})\s+" +
            @"(?:LLC|Inc\.?|Corp\.?|Ltd\.?|GmbH)\b", RegexOptions.Compiled);

    private static readonly (Regex Pattern, string Label)[] SimplePatterns =
    {
        (PasswordPattern, "Пароль?"),
        (LoginPattern, "Логин?"),
        (EmailPattern, "Email?"),
        (LooseEmailPattern, "Email?"),
        (UrlPattern, "Сайт?"),
        (StandaloneNameLabelPattern, NameLabel),
        (ExecutorSurnamePattern, NameLabel),
        (AddressPattern, "Почтовый адрес?"),
        (CityAddressPattern, "Почтовый адрес?"),
        (BirthDatePattern, "Дата рождения?"),
        (OgrnPattern, "ОГРН?"),
        (OrgRequisitesPattern, "Реквизиты организации?"),
        (BankAccountPattern, "Банковский счёт?"),
        (InnLabeledPattern, "ИНН?"),
        (LabeledPhonePattern, "Телефон?"),
        (PassportPattern, "Номер паспорта?"),
        (PassportLabeledPattern, "Номер паспорта?"),
        (PassportIssuedPattern, "Паспорт: кем выдан?"),
        (PassportIssuerBeforeCodePattern, "Паспорт: кем выдан?"),
        (PassportIssuerFieldPattern, "Паспорт: кем выдан?"),
        (PassportIssueDatePattern, "Паспорт: дата выдачи?"),
        (PassportDivisionCodePattern, "Паспорт: код подразделения?"),
        (TabNumberPattern, "Табельный номер?"),
        (PhonePattern, "Телефон?"),
        (CityPhonePattern, "Телефон?"),
        (SnilsPattern, "СНИЛС?"),
        (InnPattern, "ИНН?"),
        (CompanyQuotedPattern, "Компания?"),
        (OrgDescriptorQuotedPattern, "Организация?"),
        (OrgDescriptorPattern, "Организация?"),
        (CompanyUnquotedPattern, "Компания?"),
        (CompanyFormAfterPattern, "Компания?"),
        (CompanySuffixPattern, "Компания?"),
    };

    private static readonly (Regex Pattern, string Label)[] FullFieldPatterns =
    {
        (new Regex(@"^\s*м[её]сто\s+работ\w*(?:\s*,?\s*должност\w*)?\s*[:\-–—]?\s*(?<secret>\S.*)?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase), "Место работы?"),
        (new Regex(@"^\s*адрес\w*\s+фактическ\w*\s+(?:проживани\w*|пребывани\w*)(?:\s*\([^)]*\))?\s*[:\-–—]?\s*(?<secret>\S.*)?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase), "Адрес?"),
        (new Regex(@"^\s*м[её]сто\s+регистраци\w*\s*[:\-–—]?\s*(?<secret>\S.*)?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase), "Адрес регистрации?"),
        (new Regex(@"^\s*адрес\w*\s+регистраци\w*\s*[:\-–—]?\s*(?<secret>\S.*)?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase), "Адрес регистрации?"),

        (new Regex(@"^\s*адреса?\s*:\s*(?<secret>\S.*)?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase), "Адрес учреждения?"),
    };

    private static readonly HashSet<string> FullFieldLabels = new(StringComparer.Ordinal)
    {
        "Место работы?", "Адрес?", "Адрес регистрации?",
        "Адрес учреждения?",
    };
}
