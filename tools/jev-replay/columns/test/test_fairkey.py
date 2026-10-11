# Self-test for the fair answer key (fairkey.py). No network, no model files: synthetic Contoso-style columns only.
#   python3 -m unittest discover -s tools/jev-replay/columns/test -p 'test_*.py'
# Needs the same interpreter as gather-fair.py (pycountry and geonamescache installed).
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
import fairkey as K  # noqa: E402


def col(name, dt, **over):
    c = {'name': name, 'dataType': dt, 'isHidden': False, 'summarizeBy': 'default', 'dataCategory': None,
         'sortByColumn': None, 'formatString': None, 'isKey': False, 'relationshipRole': 'none', 'usedByMeasures': 0,
         'usedAsSortTarget': False}
    c.update(over)
    return c


MONTHS = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October',
          'November', 'December']


class DataCategory(unittest.TestCase):
    def test_country_names_are_country_whatever_the_author_said(self):
        v = ['France', 'Germany', 'Spain', 'Italy', 'Japan', 'Canada', 'Brazil', 'India', 'Kenya', 'Peru']
        self.assertEqual(K.datacat_key(col('Region', 'string', dataCategory='City'), v)[0], 'Country')

    def test_country_codes_that_are_also_state_codes_are_ambiguous_and_the_author_decides(self):
        v = ['CA', 'IN', 'DE', 'MA', 'GA', 'AL', 'CO', 'ME', 'PA', 'ID']
        self.assertEqual(K.datacat_key(col('Code', 'string', dataCategory='StateOrProvince'), v)[0], 'StateOrProvince')

    def test_us_states_are_state(self):
        v = ['Texas', 'Ohio', 'Oregon', 'Utah', 'Nevada', 'Iowa', 'Maine', 'Idaho', 'Kansas', 'Alaska']
        self.assertEqual(K.datacat_key(col('Where', 'string'), v)[0], 'StateOrProvince')

    def test_cities_are_city(self):
        v = ['Seattle', 'Chicago', 'Boston', 'Denver', 'Dallas', 'Houston', 'Portland', 'Atlanta', 'Miami', 'Phoenix']
        self.assertEqual(K.datacat_key(col('Town', 'string'), v)[0], 'City')

    def test_product_names_are_none_even_when_the_author_tagged_them(self):
        v = ['Road Bike', 'Helmet', 'Gloves', 'Chain', 'Pedal', 'Saddle', 'Tyre', 'Pump', 'Lock', 'Light']
        self.assertEqual(K.datacat_key(col('Product', 'string', dataCategory='City'), v)[0], 'none')

    def test_place_is_not_data_checkable_so_the_author_counts(self):
        v = ['Main Campus', 'North Depot', 'Harbour Site', 'Airport Store']
        self.assertEqual(K.datacat_key(col('Site', 'string', dataCategory='Place'), v)[0], 'Place')

    def test_web_and_image_links(self):
        web = ['https://contoso.com/a', 'https://contoso.com/b', 'http://www.contoso.com/c']
        img = ['https://contoso.com/a.png', 'https://contoso.com/b.jpg', 'https://contoso.com/c.png']
        self.assertEqual(K.datacat_key(col('Link', 'string'), web)[0], 'WebUrl')
        self.assertEqual(K.datacat_key(col('Link', 'string'), img)[0], 'ImageUrl')
        self.assertEqual(K.datacat_key(col('Pic', 'string', dataCategory='ImageUrl'), web)[0], 'ImageUrl')

    def test_postal_code_shape_needs_the_name_or_the_author(self):
        v = ['98052', '10001', '60601', '73301', '02108', '94105']
        self.assertEqual(K.datacat_key(col('Zip', 'string'), v)[0], 'PostalCode')
        self.assertEqual(K.datacat_key(col('Ref', 'string'), v)[0], 'none')

    def test_latitude_and_longitude_by_range_and_name(self):
        lat = [47.6, 40.7, 34.05, 41.88, -33.87]
        lon = [-122.3, -74.0, -118.2, -87.6, 151.2]
        self.assertEqual(K.datacat_key(col('Lat', 'double'), lat)[0], 'Latitude')
        self.assertEqual(K.datacat_key(col('Lon', 'double'), lon)[0], 'Longitude')
        # A value above 90 cannot be a latitude, whatever the tag says.
        self.assertEqual(K.datacat_key(col('Where', 'double', dataCategory='Latitude'), lon)[0], 'Longitude')
        self.assertEqual(K.datacat_key(col('Discount', 'double'), [0.1, 0.2, 0.05])[0], 'none')


class Summarize(unittest.TestCase):
    def test_keys_years_periods_and_row_ids_are_none(self):
        self.assertEqual(K.summarize_key(col('Units', 'int64', relationshipRole='many'), [1, 2, 3], 3)[0], 'none')
        self.assertEqual(K.summarize_key(col('Fiscal Year', 'int64'), [2019, 2020, 2021], 100)[0], 'none')
        self.assertEqual(K.summarize_key(col('Week Number', 'int64'), list(range(1, 53)), 400)[0], 'none')
        self.assertEqual(K.summarize_key(col('Line', 'int64'), list(range(1, 101)), 100)[0], 'none')

    def test_an_amount_unique_on_every_row_is_still_summed(self):
        v = [10.5, 20.25, 13.1, 99.99, 5.0]
        self.assertEqual(K.summarize_key(col('Sales Amount', 'double'), v, 5)[0], 'sum')

    def test_rates_ratios_prices_are_averaged(self):
        self.assertEqual(K.summarize_key(col('Unit Price', 'double'), [1.5, 2.5, 9.99], 50)[0], 'average')
        self.assertEqual(K.summarize_key(col('Discount', 'double'), [0.05, 0.1, 0.15, 0.2], 50)[0], 'average')
        self.assertEqual(K.summarize_key(col('Margin %', 'double'), [0.3, 0.4, 0.5], 50)[0], 'average')

    def test_quantities_are_summed(self):
        self.assertEqual(K.summarize_key(col('Order Quantity', 'int64'), [1, 2, 3, 5, 8], 900)[0], 'sum')


class Hidden(unittest.TestCase):
    def test_relationship_keys_and_ids_are_hidden(self):
        self.assertTrue(K.hidden_key(col('Customer Ref', 'int64', relationshipRole='many'), [1, 2], 10)[0])
        self.assertTrue(K.hidden_key(col('ProductKey', 'int64', relationshipRole='one'), [1, 2], 2)[0])
        self.assertTrue(K.hidden_key(col('RowGuid', 'string'), ['a', 'b'], 2)[0])
        self.assertTrue(K.hidden_key(col('Order ID', 'int64'), [5, 9], 2)[0])

    def test_a_date_on_the_one_side_and_a_named_attribute_stay_visible(self):
        self.assertFalse(K.hidden_key(col('Date', 'dateTime', relationshipRole='one'), [1, 2], 2)[0])
        self.assertFalse(K.hidden_key(col('Country', 'string', relationshipRole='one'), ['France', 'Spain'], 2)[0])
        self.assertFalse(K.hidden_key(col('Color', 'string'), ['Red', 'Blue'], 20)[0])

    def test_ambiguous_rows_take_the_author(self):
        self.assertTrue(K.hidden_key(col('Month Number', 'int64', usedAsSortTarget=True, isHidden=True), [1, 2], 12)[0])
        self.assertFalse(K.hidden_key(col('Month Number', 'int64', usedAsSortTarget=True), [1, 2], 12)[0])
        self.assertTrue(K.hidden_key(col('Month Sort', 'int64', usedAsSortTarget=True), [1, 2], 12)[0])
        self.assertTrue(K.hidden_key(col('Amount', 'double', usedByMeasures=2, isHidden=True), [1.5], 9, table_measures=3)[0])


class Sort(unittest.TestCase):
    def test_month_names_need_a_sort_by_their_numeric_partner(self):
        x = col('Month', 'string')
        pairs = {'Month No': ('int64', dict(zip(MONTHS, range(1, 13)))),
                 'Month Code': ('string', dict(zip(MONTHS, [m[:3].upper() for m in MONTHS])))}
        need, ok, _ = K.sort_key(x, MONTHS, pairs)
        self.assertTrue(need)
        self.assertEqual(ok, ['Month No'])

    def test_alphabetical_data_needs_no_sort(self):
        x = col('Colour', 'string')
        v = ['Black', 'Blue', 'Red']
        need, ok, _ = K.sort_key(x, v, {'Colour Key': ('int64', {'Black': 1, 'Blue': 2, 'Red': 3})})
        self.assertFalse(need)

    def test_entity_names_with_an_id_partner_need_no_sort(self):
        x = col('Product', 'string', sortByColumn='Product Key')
        v = ['Pedal', 'Chain', 'Helmet']
        need, _, why = K.sort_key(x, v, {'Product Key': ('int64', {'Pedal': 1, 'Chain': 2, 'Helmet': 3})})
        self.assertFalse(need, why)

    def test_ordered_bands_need_a_sort(self):
        x = col('Age Band', 'string')
        v = ['0-10', '11-20', '100+', '21-50', '51-99']
        order = {'0-10': 1, '11-20': 2, '21-50': 3, '51-99': 4, '100+': 5}
        need, ok, _ = K.sort_key(x, v, {'Band Order': ('int64', order), 'Band Key': ('int64', {k: 9 - n for k, n in order.items()})})
        self.assertTrue(need)
        self.assertEqual(ok, ['Band Order'])

    def test_ranked_words_and_a_custom_order_column(self):
        x = col('Priority', 'string')
        v = ['High', 'Low', 'Medium']
        need, ok, _ = K.sort_key(x, v, {'Priority Rank': ('int64', {'Low': 1, 'Medium': 2, 'High': 3})})
        self.assertTrue(need)
        self.assertEqual(ok, ['Priority Rank'])
        region = col('Region', 'string', sortByColumn='Region Sort')
        need, ok, _ = K.sort_key(region, ['North', 'South', 'East'], {'Region Sort': ('int64', {'North': 1, 'South': 2, 'East': 3})})
        self.assertTrue(need, 'an author-built ordering column is the order')
        self.assertEqual(ok, ['Region Sort'])

    def test_day_names_accept_a_week_starting_on_any_day(self):
        days = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday']
        sun = {d: (i + 1) % 7 for i, d in enumerate(days)}
        need, ok, _ = K.sort_key(col('Weekday', 'string'), days, {'Day Of Week': ('int64', sun)})
        self.assertTrue(need)
        self.assertEqual(ok, ['Day Of Week'])


class Format(unittest.TestCase):
    def test_format_from_type_values_and_name(self):
        self.assertEqual(K.format_key(col('Order Date', 'dateTime'), [])[0], 'date_or_time')
        self.assertEqual(K.format_key(col('Quantity', 'int64'), [1, 2])[0], 'whole_number')
        self.assertEqual(K.format_key(col('Sales Amount', 'double'), [1.5, 2.25])[0], 'currency')
        self.assertEqual(K.format_key(col('Weight', 'double'), [1.5, 22.25])[0], 'decimal')
        self.assertEqual(K.format_key(col('Discount Rate', 'double'), [0.05, 0.1])[0], 'percent')
        self.assertEqual(K.format_key(col('Score', 'double', formatString='\\$#,0.00'), [1.5])[0], 'currency', 'the author knows money')


class FromTheFirstLookAtTheKey(unittest.TestCase):
    # Each case below is a wrong key label found by reading the first census of the fresh set, before any Jev answer.
    def test_a_count_of_ratings_is_summed(self):
        self.assertEqual(K.summarize_key(col('Ratings Count', 'int64'), [100, 15, 20], 50)[0], 'sum')

    def test_ids_in_other_spellings_and_levels_are_not_summed(self):
        self.assertEqual(K.summarize_key(col('IdsCliente', 'int64'), [1, 2, 30], 90)[0], 'none')
        self.assertEqual(K.summarize_key(col('OrganizationLevel', 'int64'), [1, 2, 3, 4], 290)[0], 'none')

    def test_body_measurements_are_averaged(self):
        self.assertEqual(K.summarize_key(col('Height', 'double'), [61.5, 65.5, 70.2], 90)[0], 'average')

    def test_snake_case_and_leading_ids_are_hidden_keys(self):
        self.assertTrue(K.hidden_key(col('treatment_id', 'string', relationshipRole='one'), ['T001', 'T026'], 2)[0])
        self.assertTrue(K.hidden_key(col('ID Factura', 'string', relationshipRole='one'), ['F0001', 'F0006'], 2)[0])

    def test_compass_regions_are_not_states(self):
        v = ['east', 'midwest', 'north', 'south', 'west']
        self.assertEqual(K.datacat_key(col('stateregion', 'string'), v)[0], 'none')

    def test_an_order_number_is_not_an_ordering_column(self):
        v = ['PO100', 'PO1244', 'PO1455']
        need, _, _ = K.sort_key(col('CustomerPONumber', 'string'), v,
                                {'SalesOrderNumber': ('string', {'PO100': 'SO9', 'PO1244': 'SO1', 'PO1455': 'SO5'})})
        self.assertFalse(need)

    def test_an_id_stored_as_text_needs_no_sort(self):
        v = ['10708100', '231203233', '7201901']
        need, _, _ = K.sort_key(col('NationalIDNumber', 'string'), v, {'EmployeeKey': ('int64', {'10708100': 1, '231203233': 2, '7201901': 3})})
        self.assertFalse(need)

    def test_a_percent_name_wins_over_a_money_word(self):
        self.assertEqual(K.format_key(col('UnitPriceDiscountPct', 'double'), [0.0])[0], 'percent')



class FromTheSecondLookAtTheKey(unittest.TestCase):
    # Wrong key labels found in the census of the full fresh set (218 models), still before any Jev answer.
    def test_spanish_day_names_are_days(self):
        v = ['Lunes', 'Martes', 'Mi\u00e9rcoles', 'Jueves', 'Viernes']
        need, ok, _ = K.sort_key(col('D\u00edaNombre', 'string'), v, {'D\u00edaID': ('int64', dict(zip(v, range(1, 6))))})
        self.assertTrue(need)
        self.assertEqual(ok, ['D\u00edaID'])

    def test_an_unknown_bucket_does_not_hide_a_vocabulary(self):
        v = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday', 'Unknown']
        need, ok, _ = K.sort_key(col('weekday_name', 'string'), v, {'weekday_sort': ('int64', dict(zip(v, range(1, 9))))})
        self.assertTrue(need)

    def test_dates_stored_as_text_need_a_sort(self):
        v = ['01-Apr-2014', '04-Nov-2013', '08-Mar-2018']
        need, ok, _ = K.sort_key(col('SlicerDate', 'string'), v, {'DateKey': ('int64', {'01-Apr-2014': 20140401, '04-Nov-2013': 20131104, '08-Mar-2018': 20180308})})
        self.assertTrue(need)
        self.assertEqual(ok, ['DateKey'])

    def test_mail_links_and_data_images(self):
        self.assertEqual(K.datacat_key(col('MailTo', 'string'), ['mailto:a@contoso.com', 'mailto:b@contoso.com'])[0], 'WebUrl')
        self.assertEqual(K.datacat_key(col('Icon', 'string'), ["data:image/svg+xml;utf8,<svg/>", "data:image/png;base64,AAA"])[0], 'ImageUrl')

    def test_coordinates_stored_as_text(self):
        self.assertEqual(K.datacat_key(col('Latitude', 'string'), ['-0.0236', '29.86', '33.46'])[0], 'Latitude')



class FromTheFirstHandCheck(unittest.TestCase):
    # Classes of wrong key label the first blind hand check exposed (its sample is not reused to measure the fix).
    def test_substrings_are_not_words(self):
        self.assertEqual(K.summarize_key(col('Operating Profit', 'double'), [1.5, 2.5], 50)[0], 'sum')
        self.assertEqual(K.summarize_key(col('Trip Duration', 'double'), [1.5, 2.5], 50)[0], 'sum')
        self.assertEqual(K.summarize_key(col('Landed Conversions', 'int64'), [1, 5], 50)[0], 'sum')
        self.assertEqual(K.summarize_key(col('TaxRate', 'double'), [0.1, 0.2], 50)[0], 'average')

    def test_amounts_named_for_a_year_and_plural_periods_are_not_labels(self):
        self.assertEqual(K.summarize_key(col('SalesLastYear', 'double'), [100.5, 200.25], 50)[0], 'sum')
        self.assertEqual(K.summarize_key(col('Planned Hours', 'int64'), [1, 8, 40], 50)[0], 'sum')
        self.assertEqual(K.summarize_key(col('YearsSinceLastPromotion', 'int64'), [0, 1, 5], 900)[0], 'average')

    def test_codes_coordinates_thresholds_offsets_and_dates_as_numbers_are_labels(self):
        for n in ('fav_cuisine_coded', 'Area Code (M49)', 'c_phone', 'X', 'Y', 'Long', 'MaxQty', 'MonthOffset', 'Order Date'):
            self.assertEqual(K.summarize_key(col(n, 'int64'), [1, 2, 3], 90)[0], 'none', n)

    def test_scores_and_durations_per_person_are_averaged(self):
        for n in ('EnvironmentSatisfaction', 'JobInvolvement', 'tenure', 'Educational_Quality_Index', 'years_experience'):
            self.assertEqual(K.summarize_key(col(n, 'int64'), [1, 2, 3, 4], 900)[0], 'average', n)

    def test_power_query_leftovers_and_sort_helpers_by_name_are_hidden(self):
        for n in ('Column12', 'Columna4', 'Folder Path', 'stateid', 'IdsCategoria'):
            self.assertTrue(K.hidden_key(col(n, 'string'), ['a'], 5)[0], n)
        self.assertTrue(K.hidden_key(col('Sort Order', 'int64'), [1, 2], 5)[0])
        self.assertFalse(K.hidden_key(col('Paid', 'string'), ['Yes'], 5)[0], 'paid is not an id')

    def test_surnames_are_not_counties(self):
        v = ['Adams', 'Jackson', 'Lincoln', 'Madison', 'Monroe', 'Washington', 'Grant', 'Clay']
        self.assertEqual(K.datacat_key(col('last_name', 'string'), v)[0], 'none')
        self.assertEqual(K.datacat_key(col('County', 'string'), v)[0], 'County')

    def test_six_digit_postal_codes_and_cogs(self):
        self.assertEqual(K.datacat_key(col('Postal Code', 'int64'), [110001, 400001, 560001])[0], 'PostalCode')
        self.assertEqual(K.format_key(col('COGS', 'double'), [10.5, 20.25])[0], 'currency')


if __name__ == '__main__':
    unittest.main()
