/*ETactileKit - Visualization and Execution programme

LoadData :- used to load data from JSON files

Functions:  load()
*/

import java.util.ArrayList;
import java.util.Collections;
import java.util.Comparator;

class LoadData {
  private JSONObject  json_pattern_data;
  private JSONObject  json_array_data;
  
  private JSONArray   patterns;
  private boolean     simulation;
  private boolean     execution;
  
  private JSONArray   coordinates, electrodes;
  private int         electrodes_number;
  private byte[]      mapping_func;



  public void load(String array_file, String pattern_file) {
    try {
      json_pattern_data    = loadJSONObject(pattern_file);
      patterns             = json_pattern_data.getJSONArray("Pattern_Data");
      simulation           = json_pattern_data.getBoolean("Simulation");
      execution            = json_pattern_data.getBoolean("Execution");

      json_array_data      = loadJSONObject(array_file);
      coordinates          = json_array_data.getJSONArray("outline");
      electrodes           = json_array_data.getJSONArray("electrodes");
      electrodes_number    = json_array_data.getInt("Electrodes_Number");
      
      //sortElectrodesByNumber();
    
      // Load Mapping_func array from the JSON file
      JSONArray mapping_array = json_array_data.getJSONArray("Mapping_func");
      
      mapping_func = new byte[mapping_array.size()];
      for (int i = 0; i < mapping_array.size(); i++) {
        mapping_func[i] = (byte) mapping_array.getInt(i);
      }   
    } catch (Exception e) {
      println("Error loading JSON data: " + e);
      simulation        = false;
      execution         = false;
      electrodes_number = 0;
      patterns          = null;
    }
  }
  
    void sortElectrodesByNumber() {
    ArrayList<JSONObject> electrodes_list = new ArrayList<JSONObject>();
    
    // Convert JSONArray to ArrayList
    for (int i = 0; i < electrodes.size(); i++) {
      electrodes_list.add(electrodes.getJSONObject(i));
    }
    
    // Sort ArrayList based on the number field
    Collections.sort(electrodes_list, new Comparator<JSONObject>() {
      @Override
      public int compare(JSONObject o1, JSONObject o2) {
        return Integer.compare(o1.getInt("number"), o2.getInt("number"));
      }
    });
    
    // Create a new JSONArray from the sorted list
    electrodes = new JSONArray();
    for (JSONObject electrode : electrodes_list) {
      electrodes.append(electrode);
    }
  }
}
