/*ETactileKit - User defined pattern generation  programme

LoadData :- used to load electrode array details from the JSON file

Functions:  LoadData()
*/

class LoadData {
  
  JSONObject json_array_Data;
  JSONArray Electrode_Array;
  
  int Electrodes_number;
  

  void load() {
    try {
      json_array_Data = loadJSONObject("electrode_array_01.json");
      Electrode_Array = json_array_Data.getJSONArray("Array");
      Electrodes_number = json_array_Data.getInt("Electrodes_Number");
      
    } catch (Exception e) {
      println("Error loading JSON data: " + e);
      Electrodes_number = 0;
      
    }
  }
}
